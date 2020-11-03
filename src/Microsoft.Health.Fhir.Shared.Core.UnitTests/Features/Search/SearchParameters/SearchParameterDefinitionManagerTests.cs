﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Parameters;
using Microsoft.Health.Fhir.Core.Features.Search.Registry;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Core.UnitTests.Features.Context;
using Microsoft.Health.Fhir.ValueSets;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Search
{
    public class SearchParameterDefinitionManagerTests : IAsyncLifetime
    {
        private static readonly string ResourceId = "http://hl7.org/fhir/SearchParameter/Resource-id";
        private static readonly string ResourceLastUpdated = "http://hl7.org/fhir/SearchParameter/Resource-lastUpdated";
        private static readonly string ResourceProfile = "http://hl7.org/fhir/SearchParameter/Resource-profile";
        private static readonly string ResourceSecurity = "http://hl7.org/fhir/SearchParameter/Resource-security";
        private static readonly string ResourceQuery = "http://hl7.org/fhir/SearchParameter/Resource-query";
        private static readonly string ResourceTest = "http://hl7.org/fhir/SearchParameter/Resource-test";

        private readonly SearchParameterStatusManager _manager;
        private readonly ISearchParameterRegistry _searchParameterRegistry;
        private readonly SearchParameterDefinitionManager _searchParameterDefinitionManager;
        private readonly IMediator _mediator;
        private readonly SearchParameterInfo[] _searchParameterInfos;
        private readonly SearchParameterInfo _queryParameter;
        private readonly SearchParameterInfo _testSearchParamInfo;
        private readonly ISearchParameterSupportResolver _searchParameterSupportResolver;
        private readonly IFhirRequestContextAccessor _fhirRequestContextAccessor;
        private readonly IFhirRequestContext _fhirRequestContext = new DefaultFhirRequestContext();

        public SearchParameterDefinitionManagerTests()
        {
            _searchParameterSupportResolver = Substitute.For<ISearchParameterSupportResolver>();
            _mediator = Substitute.For<IMediator>();
            _searchParameterRegistry = Substitute.For<ISearchParameterRegistry>();
            _searchParameterDefinitionManager = new SearchParameterDefinitionManager(ModelInfoProvider.Instance);
            _fhirRequestContextAccessor = Substitute.For<IFhirRequestContextAccessor>();
            _fhirRequestContextAccessor.FhirRequestContext.Returns(_fhirRequestContext);

            _manager = new SearchParameterStatusManager(
                _searchParameterRegistry,
                _searchParameterDefinitionManager,
                _searchParameterSupportResolver,
                _mediator);

            _searchParameterRegistry.GetSearchParameterStatuses()
                .Returns(new[]
                {
                    new ResourceSearchParameterStatus
                    {
                        Status = SearchParameterStatus.Enabled,
                        Uri = new Uri(ResourceId),
                    },
                    new ResourceSearchParameterStatus
                    {
                        Status = SearchParameterStatus.Enabled,
                        Uri = new Uri(ResourceLastUpdated),
                        IsPartiallySupported = true,
                    },
                    new ResourceSearchParameterStatus
                    {
                        Status = SearchParameterStatus.Disabled,
                        Uri = new Uri(ResourceProfile),
                    },
                    new ResourceSearchParameterStatus
                    {
                        Status = SearchParameterStatus.Supported,
                        Uri = new Uri(ResourceSecurity),
                    },
                });

            _queryParameter = new SearchParameterInfo("_query", SearchParamType.Token, new Uri(ResourceQuery));
            _searchParameterInfos = new[]
            {
                new SearchParameterInfo("_id", SearchParamType.Token, new Uri(ResourceId)),
                new SearchParameterInfo("_lastUpdated", SearchParamType.Token, new Uri(ResourceLastUpdated)),
                new SearchParameterInfo("_profile", SearchParamType.Token, new Uri(ResourceProfile)),
                new SearchParameterInfo("_security", SearchParamType.Token, new Uri(ResourceSecurity)),
                _queryParameter,
            };

            _testSearchParamInfo = new SearchParameterInfo("_test", SearchParamType.Special, new Uri(ResourceTest));

            _searchParameterSupportResolver
                .IsSearchParameterSupported(Arg.Any<SearchParameterInfo>())
                .Returns((false, false));

            _searchParameterSupportResolver
                .IsSearchParameterSupported(Arg.Is(_searchParameterInfos[4]))
                .Returns((true, false));
        }

        public async Task InitializeAsync()
        {
            await _searchParameterDefinitionManager.StartAsync(CancellationToken.None);
        }

        public Task DisposeAsync() => Task.CompletedTask;

        [Fact]
        public async Task GivenSupportedParams_WhenGettingSupported_ThenSupportedParamsReturned()
        {
            await _manager.EnsureInitialized();
            var supportedDefinitionManager = new SupportedSearchParameterDefinitionManager(_searchParameterDefinitionManager);
            var paramList = supportedDefinitionManager.GetSupportedButNotSearchableParams();

            Assert.Single(paramList);
            Assert.Collection(paramList, p =>
            {
                Assert.True(p.IsSupported);
                Assert.False(p.IsSearchable);
            });
        }

        [Fact]
        public async Task GivenSearchableParams_WhenGettingSearchable_ThenCorrectParamsReturned()
        {
            await _manager.EnsureInitialized();
            var searchableDefinitionManager = new SearchableSearchParameterDefinitionManager(_searchParameterDefinitionManager, _fhirRequestContextAccessor);
            var paramList = searchableDefinitionManager.AllSearchParameters;

            Assert.Collection(
                paramList,
                p =>
                {
                    Assert.True(p.IsSupported);
                    Assert.True(p.IsSearchable);
                },
                p2 =>
                {
                    Assert.True(p2.IsSupported);
                    Assert.True(p2.IsSearchable);
                });
        }

        [Fact]
        public async Task GivenContextToIncludePatialIndexedParams_WhenGettingSearchable_ThenCorrectParamsReturned()
        {
            await _manager.EnsureInitialized();
            _fhirRequestContext.IncludePartiallyIndexedSearchParams = true;
            var searchableDefinitionManager = new SearchableSearchParameterDefinitionManager(_searchParameterDefinitionManager, _fhirRequestContextAccessor);
            var paramList = searchableDefinitionManager.AllSearchParameters;

            Assert.Collection(
                paramList,
                p =>
                {
                    Assert.True(p.IsSupported);
                    Assert.True(p.IsSearchable);
                },
                p2 =>
                {
                    Assert.True(p2.IsSupported);
                    Assert.True(p2.IsSearchable);
                },
                p3 =>
                {
                    Assert.True(p3.IsSupported);
                    Assert.False(p3.IsSearchable);
                });

            _fhirRequestContext.IncludePartiallyIndexedSearchParams = false;
        }

        [Fact]
        public async Task GivenNoHeaderForPatiallyIndexedParams_WhenSearchingSupportedParameterByName_ThenExceptionThrown()
        {
            await _manager.EnsureInitialized();
            _fhirRequestContext.IncludePartiallyIndexedSearchParams = false;
            var searchableDefinitionManager = new SearchableSearchParameterDefinitionManager(_searchParameterDefinitionManager, _fhirRequestContextAccessor);

            Assert.Throws<SearchParameterNotSupportedException>(() => searchableDefinitionManager.GetSearchParameter(new Uri(ResourceSecurity)));

            _fhirRequestContext.IncludePartiallyIndexedSearchParams = false;
        }

        [Fact]
        public async Task GivenHeaderToIncludePatialIndexedParams_WhenSearchingSupportedParameterByName_ThenSupportedParamsReturned()
        {
            await _manager.EnsureInitialized();
            _fhirRequestContext.IncludePartiallyIndexedSearchParams = true;
            var searchableDefinitionManager = new SearchableSearchParameterDefinitionManager(_searchParameterDefinitionManager, _fhirRequestContextAccessor);

            var param = searchableDefinitionManager.GetSearchParameter(new Uri(ResourceSecurity));
            SearchParameterInfo expectedSearchParam = _searchParameterInfos[3];
            ValidateSearchParam(expectedSearchParam, param);

            _fhirRequestContext.IncludePartiallyIndexedSearchParams = false;
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenGettingExistingSearchParameterByName_CorrectParameterIsReturned()
        {
            SearchParameterInfo expectedSearchParam = _searchParameterInfos[0];
            SearchParameterInfo actualSearchParam = _searchParameterDefinitionManager.GetSearchParameter(
                "SearchParameter",
                expectedSearchParam.Name);

            ValidateSearchParam(expectedSearchParam, actualSearchParam);
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenGettingNonexistentSearchParameterByName_ExceptionIsThrown()
        {
            Assert.Throws<SearchParameterNotSupportedException>(() => _searchParameterDefinitionManager.GetSearchParameter(
                "SearchParameter",
                _testSearchParamInfo.Name));
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenTryingToGetExistingSearchParameter_ReturnsTrue()
        {
            SearchParameterInfo expectedSearchParam = _searchParameterInfos[0];
            Assert.True(_searchParameterDefinitionManager.TryGetSearchParameter(
                "SearchParameter",
                expectedSearchParam.Name,
                out SearchParameterInfo actualSearchParam));

            ValidateSearchParam(expectedSearchParam, actualSearchParam);
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenTryingToGetNonexistentSearchParameter_ReturnsFalse()
        {
            Assert.False(_searchParameterDefinitionManager.TryGetSearchParameter(
                "SearchParameter",
                _testSearchParamInfo.Name,
                out SearchParameterInfo _));
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenGettingExistingSearchParameterByUrl_CorrectParameterIsReturned()
        {
            SearchParameterInfo expectedSearchParam = _searchParameterInfos[0];
            SearchParameterInfo actualSearchParam = _searchParameterDefinitionManager.GetSearchParameter(expectedSearchParam.Url);

            ValidateSearchParam(expectedSearchParam, actualSearchParam);
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenGettingNonexistentSearchParameterByUrl_ExceptionIsThrown()
        {
            Assert.Throws<SearchParameterNotSupportedException>(() => _searchParameterDefinitionManager.GetSearchParameter(_testSearchParamInfo.Url));
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenAddingNewSearchParameterHash_SearchParamHashMapContainsTheNewData()
        {
            // Validate that initial search param map is empty
            Assert.Equal(0, _searchParameterDefinitionManager.SearchParameterHashMap.Count);

            // Add new resource type - search parameter hash information.
            Dictionary<string, string> hashMap = new Dictionary<string, string>
            {
                { "Patient", "patientHash1" },
                { "Observation", "observationHash1" },
            };

            _searchParameterDefinitionManager.UpdateSearchParameterHashMap(hashMap);

            var searchParamMap = _searchParameterDefinitionManager.SearchParameterHashMap;
            Assert.Equal(2, searchParamMap.Count);
            Assert.Equal("patientHash1", searchParamMap["Patient"]);
            Assert.Equal("observationHash1", searchParamMap["Observation"]);
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenUpdatingExistingParameters_SearchParamHashMapContainsTheUpdatedData()
        {
            // Initialize search parameter hash map
            var hashMap = new Dictionary<string, string>
            {
                { "Patient", "patientHash1" },
                { "Observation", "observationHash1" },
            };
            _searchParameterDefinitionManager.UpdateSearchParameterHashMap(hashMap);

            // Update existing resource type with new hash
            _searchParameterDefinitionManager.UpdateSearchParameterHashMap(new Dictionary<string, string>() { { "Patient", "patientHash2" } });

            IReadOnlyDictionary<string, string> searchParamMap = _searchParameterDefinitionManager.SearchParameterHashMap;
            Assert.Equal(2, searchParamMap.Count);
            Assert.Equal("patientHash2", searchParamMap["Patient"]);
            Assert.Equal("observationHash1", searchParamMap["Observation"]);
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenGettingSearchParameterHashForExistingResourceType_ThenHashIsReturned()
        {
            // Initialize search parameter hash map
            var hashMap = new Dictionary<string, string>
            {
                { "Patient", "patientHash1" },
                { "Observation", "observationHash1" },
            };
            _searchParameterDefinitionManager.UpdateSearchParameterHashMap(hashMap);

            var searchParamHash = _searchParameterDefinitionManager.GetSearchParameterHashForResourceType("Patient");
            Assert.Equal("patientHash1", searchParamHash);
        }

        [Fact]
        public void GivenASearchParameterDefinitionManager_WhenGettingSearchParameterHashForMissingResourceType_ThenNullIsReturned()
        {
            // Initialize search parameter hash map
            var hashMap = new Dictionary<string, string>
            {
                { "Patient", "patientHash1" },
                { "Observation", "observationHash1" },
            };
            _searchParameterDefinitionManager.UpdateSearchParameterHashMap(hashMap);

            var searchParamHash = _searchParameterDefinitionManager.GetSearchParameterHashForResourceType("Medication");
            Assert.Null(searchParamHash);
        }

        [Fact]
        public void GivenSearchParameter_WhenGettingSearchParameterType_CorrectTypeIsReturned()
        {
            SearchParameterInfo searchParam = _searchParameterInfos[0];
            SearchParamType type = _searchParameterDefinitionManager.GetSearchParameterType(searchParam, null);

            Assert.Equal(searchParam.Type, type);
        }

        private static void ValidateSearchParam(SearchParameterInfo expectedSearchParam, SearchParameterInfo actualSearchParam)
        {
            Assert.Equal(expectedSearchParam.Name, actualSearchParam.Name);
            Assert.Equal(expectedSearchParam.Type, actualSearchParam.Type);
            Assert.Equal(expectedSearchParam.Url, actualSearchParam.Url);
        }
    }
}
