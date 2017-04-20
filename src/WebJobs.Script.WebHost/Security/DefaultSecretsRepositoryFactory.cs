﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script.Config;

namespace Microsoft.Azure.WebJobs.Script.WebHost
{
    public sealed class DefaultSecretsRepositoryFactory : ISecretsRepositoryFactory
    {
        [CLSCompliant(false)]
        public ISecretsRepository Create(ScriptSettingsManager settingsManager, WebHostSettings webHostSettings, ScriptHostConfiguration config)
        {
            string secretStorageType = settingsManager.GetSetting(EnvironmentSettingNames.AzureWebJobsSecretStorageType);
            string storageString = AmbientConnectionStringProvider.Instance.GetConnectionString(ConnectionStringNames.Storage);
            if (secretStorageType != null && secretStorageType.Equals("Blob", StringComparison.OrdinalIgnoreCase) && storageString != null)
            {
                string siteSlotName = settingsManager.AzureWebsiteUniqueSlotName ?? config.HostConfig.HostId;
                return new BlobStorageSecretsRepository(Path.Combine(webHostSettings.SecretsPath, "Sentinels"), storageString, siteSlotName);
            }
            else
            {
                return new FileSystemSecretsRepository(webHostSettings.SecretsPath);
            }
        }
    }
}
