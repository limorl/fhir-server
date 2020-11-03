﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Client;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Search
{
    public class UriSearchTestFixture : HttpIntegrationTestFixture
    {
        public UriSearchTestFixture(DataStore dataStore, Format format, TestFhirServerFactory testFhirServerFactory)
            : base(dataStore, format, testFhirServerFactory)
        {
        }

        public IReadOnlyList<ValueSet> ValueSets { get; private set; }

        protected override async Task OnInitializedAsync()
        {
            // Prepare the resources used for URI search tests.
            await TestFhirClient.DeleteAllResources(ResourceType.ValueSet);

            ValueSets = await TestFhirClient.CreateResourcesAsync<ValueSet>(
                vs => AddValueSet(vs, "http://somewhere.com/test/system"),
                vs => AddValueSet(vs, "urn://localhost/test"));

            void AddValueSet(ValueSet vs, string url)
            {
                vs.Status = PublicationStatus.Active;
                vs.Url = url;
            }
        }
    }
}
