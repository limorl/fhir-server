﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.CosmosDb.Configs;
using Microsoft.Health.Fhir.CosmosDb.Features.Search;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.HardDelete;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.Replace;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.Upsert;
using Microsoft.Health.Fhir.ValueSets;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage
{
    public sealed class CosmosFhirDataStore : IFhirDataStore, IProvideCapability
    {
        private readonly IScoped<Container> _containerScope;
        private readonly ICosmosQueryFactory _cosmosQueryFactory;
        private readonly RetryExceptionPolicyFactory _retryExceptionPolicyFactory;
        private readonly ILogger<CosmosFhirDataStore> _logger;
        private readonly IModelInfoProvider _modelInfoProvider;

        private static readonly UpsertWithHistory _upsertWithHistoryProc = new UpsertWithHistory();
        private static readonly HardDelete _hardDelete = new HardDelete();
        private static readonly ReplaceSingleResource _replaceSingleResource = new ReplaceSingleResource();
        private readonly CoreFeatureConfiguration _coreFeatures;

        /// <summary>
        /// Initializes a new instance of the <see cref="CosmosFhirDataStore"/> class.
        /// </summary>
        /// <param name="containerScope">
        /// A function that returns an <see cref="Container"/>.
        /// Note that this is a function so that the lifetime of the instance is not directly controlled by the IoC container.
        /// </param>
        /// <param name="cosmosDataStoreConfiguration">The data store configuration.</param>
        /// <param name="namedCosmosCollectionConfigurationAccessor">The IOptions accessor to get a named version.</param>
        /// <param name="cosmosQueryFactory">The factory used to create the document query.</param>
        /// <param name="retryExceptionPolicyFactory">The retry exception policy factory.</param>
        /// <param name="logger">The logger instance.</param>
        /// <param name="modelInfoProvider">The model provider</param>
        /// <param name="coreFeatures">The core feature configuration</param>
        public CosmosFhirDataStore(
            IScoped<Container> containerScope,
            CosmosDataStoreConfiguration cosmosDataStoreConfiguration,
            IOptionsMonitor<CosmosCollectionConfiguration> namedCosmosCollectionConfigurationAccessor,
            ICosmosQueryFactory cosmosQueryFactory,
            RetryExceptionPolicyFactory retryExceptionPolicyFactory,
            ILogger<CosmosFhirDataStore> logger,
            IModelInfoProvider modelInfoProvider,
            IOptions<CoreFeatureConfiguration> coreFeatures)
        {
            EnsureArg.IsNotNull(containerScope, nameof(containerScope));
            EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
            EnsureArg.IsNotNull(namedCosmosCollectionConfigurationAccessor, nameof(namedCosmosCollectionConfigurationAccessor));
            EnsureArg.IsNotNull(cosmosQueryFactory, nameof(cosmosQueryFactory));
            EnsureArg.IsNotNull(retryExceptionPolicyFactory, nameof(retryExceptionPolicyFactory));
            EnsureArg.IsNotNull(logger, nameof(logger));
            EnsureArg.IsNotNull(modelInfoProvider, nameof(modelInfoProvider));
            EnsureArg.IsNotNull(coreFeatures, nameof(coreFeatures));

            _containerScope = containerScope;
            _cosmosQueryFactory = cosmosQueryFactory;
            _retryExceptionPolicyFactory = retryExceptionPolicyFactory;
            _logger = logger;
            _modelInfoProvider = modelInfoProvider;
            _coreFeatures = coreFeatures.Value;
        }

        public async Task<UpsertOutcome> UpsertAsync(
            ResourceWrapper resource,
            WeakETag weakETag,
            bool allowCreate,
            bool keepHistory,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));

            var cosmosWrapper = new FhirCosmosResourceWrapper(resource);

            try
            {
                _logger.LogDebug($"Upserting {resource.ResourceTypeName}/{resource.ResourceId}, ETag: \"{weakETag?.VersionId}\", AllowCreate: {allowCreate}, KeepHistory: {keepHistory}");

                UpsertWithHistoryModel response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _upsertWithHistoryProc.Execute(
                        _containerScope.Value.Scripts,
                        cosmosWrapper,
                        weakETag?.VersionId,
                        allowCreate,
                        keepHistory,
                        ct),
                    cancellationToken);

                return new UpsertOutcome(response.Wrapper, response.OutcomeType);
            }
            catch (CosmosException exception)
            {
                switch (exception.GetSubStatusCode())
                {
                    case HttpStatusCode.PreconditionFailed:
                        throw new PreconditionFailedException(string.Format(Core.Resources.ResourceVersionConflict, weakETag?.VersionId));
                    case HttpStatusCode.NotFound:
                        if (cosmosWrapper.IsDeleted)
                        {
                            return null;
                        }

                        if (weakETag != null)
                        {
                            throw new ResourceNotFoundException(string.Format(Core.Resources.ResourceNotFoundByIdAndVersion, resource.ResourceTypeName, resource.ResourceId, weakETag.VersionId));
                        }
                        else if (!allowCreate)
                        {
                            throw new MethodNotAllowedException(Core.Resources.ResourceCreationNotAllowed);
                        }

                        break;

                    case HttpStatusCode.ServiceUnavailable:
                        throw new ServiceUnavailableException();
                }

                _logger.LogError(exception, "Unhandled Document Client Exception");

                throw;
            }
        }

        public async Task<ResourceWrapper> GetAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(key, nameof(key));

            bool isVersionedRead = !string.IsNullOrEmpty(key.VersionId);

            if (isVersionedRead)
            {
                QueryDefinition sqlQuerySpec = new QueryDefinition($"select {SearchValueConstants.SelectedFields} from root r where r.resourceId = @resourceId and r.version = @version")
                    .WithParameter("@resourceId", key.Id)
                    .WithParameter("@version", key.VersionId);

                var result = await ExecuteDocumentQueryAsync<FhirCosmosResourceWrapper>(
                    sqlQuerySpec,
                    new QueryRequestOptions { PartitionKey = new PartitionKey(key.ToPartitionKey()) },
                    cancellationToken: cancellationToken);

                return result.FirstOrDefault();
            }

            try
            {
                return await _containerScope.Value
                    .ReadItemAsync<FhirCosmosResourceWrapper>(key.Id, new PartitionKey(key.ToPartitionKey()), cancellationToken: cancellationToken);
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task HardDeleteAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(key, nameof(key));

            try
            {
                _logger.LogDebug($"Obliterating {key.ResourceType}/{key.Id}");

                StoredProcedureExecuteResponse<IList<string>> response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _hardDelete.Execute(
                        _containerScope.Value.Scripts,
                        key,
                        ct),
                    cancellationToken);

                _logger.LogDebug($"Hard-deleted {response.Resource.Count} documents, which consumed {response.RequestCharge} RUs. The list of hard-deleted documents: {string.Join(", ", response.Resource)}.");
            }
            catch (CosmosException exception)
            {
                if (exception.GetSubStatusCode() == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new RequestRateExceededException(exception.RetryAfter);
                }

                _logger.LogError(exception, "Unhandled Document Client Exception");

                throw;
            }
        }

        public async Task UpdateSearchParameterHashBatchAsync(IReadOnlyCollection<ResourceWrapper> resources, CancellationToken cancellationToken)
        {
            // TODO: use batch command to update both hash values and search index values for list updateSearchIndices
            // this is a place holder update until we batch update resources
            foreach (var resource in resources)
            {
                await UpdateSearchIndexForResourceAsync(resource, WeakETag.FromVersionId(resource.Version), cancellationToken);
            }
        }

        public async Task UpdateSearchParameterIndicesBatchAsync(IReadOnlyCollection<ResourceWrapper> resources, CancellationToken cancellationToken)
        {
            // TODO: use batch command to update both hash values and search index values for list updateSearchIndices
            // this is a place holder update until we batch update resources
            foreach (var resource in resources)
            {
                await UpdateSearchIndexForResourceAsync(resource, WeakETag.FromVersionId(resource.Version), cancellationToken);
            }
        }

        public async Task<ResourceWrapper> UpdateSearchIndexForResourceAsync(ResourceWrapper resourceWrapper, WeakETag weakETag, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resourceWrapper, nameof(resourceWrapper));
            EnsureArg.IsNotNull(weakETag, nameof(weakETag));

            var cosmosWrapper = new FhirCosmosResourceWrapper(resourceWrapper);

            try
            {
                _logger.LogDebug($"Replacing {resourceWrapper.ResourceTypeName}/{resourceWrapper.ResourceId}, ETag: \"{weakETag.VersionId}\"");

                FhirCosmosResourceWrapper response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _replaceSingleResource.Execute(
                        _containerScope.Value.Scripts,
                        cosmosWrapper,
                        weakETag.VersionId,
                        ct),
                    cancellationToken);

                return response;
            }
            catch (CosmosException exception)
            {
                // Check GetSubStatusCode documentation for why we need to get that instead of the status code.
                switch (exception.GetSubStatusCode())
                {
                    case HttpStatusCode.PreconditionFailed:
                        throw new PreconditionFailedException(string.Format(Core.Resources.ResourceVersionConflict, weakETag));

                    case HttpStatusCode.NotFound:
                        throw new ResourceNotFoundException(string.Format(
                            Core.Resources.ResourceNotFoundByIdAndVersion,
                            resourceWrapper.ResourceTypeName,
                            resourceWrapper.ResourceId,
                            weakETag));

                    case HttpStatusCode.ServiceUnavailable:
                        throw new ServiceUnavailableException();
                }

                _logger.LogError(exception, "Unhandled Document Client Exception");
                throw;
            }
        }

        internal async Task<FeedResponse<T>> ExecuteDocumentQueryAsync<T>(QueryDefinition sqlQuerySpec, QueryRequestOptions feedOptions, string continuationToken = null, CancellationToken cancellationToken = default)
        {
            EnsureArg.IsNotNull(sqlQuerySpec, nameof(sqlQuerySpec));

            var context = new CosmosQueryContext(sqlQuerySpec, feedOptions, continuationToken);

            var documentQuery = _cosmosQueryFactory.Create<T>(_containerScope.Value, context);

            try
            {
                return await documentQuery.ExecuteNextAsync(cancellationToken);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.BadRequest && continuationToken != null && e.ResponseBody.StartsWith("Malformed continuation token", StringComparison.OrdinalIgnoreCase))
            {
                // there isn't a status code that indicates this condition, so we rely on the error message.
                throw new BadRequestException(Core.Resources.InvalidContinuationToken);
            }
        }

        public void Build(ICapabilityStatementBuilder builder)
        {
            EnsureArg.IsNotNull(builder, nameof(builder));

            builder.AddDefaultResourceInteractions()
                .AddDefaultSearchParameters()
                .AddDefaultRestSearchParams();

            if (_coreFeatures.SupportsBatch)
            {
                builder.AddRestInteraction(SystemRestfulInteraction.Batch);
            }
        }
    }
}
