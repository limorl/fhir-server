﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using MediatR;
using Microsoft.Health.SqlServer.Features.Schema.Messages.Notifications;

namespace Microsoft.Health.Fhir.SqlServer.Features.Storage
{
    public class SchemaUpgradedHandler : INotificationHandler<SchemaUpgradedNotification>
    {
        private SqlServerFhirModel _sqlServerFhirModel;

        public SchemaUpgradedHandler(SqlServerFhirModel sqlServerFhirModel)
        {
            EnsureArg.IsNotNull(sqlServerFhirModel, nameof(sqlServerFhirModel));

            _sqlServerFhirModel = sqlServerFhirModel;
        }

        public Task Handle(SchemaUpgradedNotification notification, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(notification, nameof(notification));

            // TODO: Use version information to call modularized start methods (work item 75557).
            int? version = notification.Version;
            _sqlServerFhirModel.Start();

            return Task.CompletedTask;
        }
    }
}
