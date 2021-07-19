// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using static Microsoft.Azure.WebJobs.Script.EnvironmentSettingNames;

namespace Microsoft.Azure.WebJobs.Script
{
    internal static class EnvironmentExtensions
    {
        // For testing
        internal static string BaseDirectory { get; set; }

        public static string GetEnvironmentVariableOrDefault(this IEnvironment environment, string name, string defaultValue)
        {
            return environment.GetEnvironmentVariable(name) ?? defaultValue;
        }

        public static bool IsLinuxMetricsPublishingEnabled(this IEnvironment environment)
        {
            return environment.IsLinuxConsumption() && string.IsNullOrEmpty(environment.GetEnvironmentVariable(ContainerStartContext));
        }

        public static bool IsPlaceholderModeEnabled(this IEnvironment environment)
        {
            return environment.GetEnvironmentVariable(AzureWebsitePlaceholderMode) == "1";
        }

        public static bool IsLegacyPlaceholderTemplateSite(this IEnvironment environment)
        {
            string siteName = environment.GetEnvironmentVariable(AzureWebsiteName);
            return string.IsNullOrEmpty(siteName) ? false : siteName.Equals(ScriptConstants.LegacyPlaceholderTemplateSiteName, StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool IsRuntimeScaleMonitoringEnabled(this IEnvironment environment)
        {
            return environment.GetEnvironmentVariable(FunctionsRuntimeScaleMonitoringEnabled) == "1";
        }

        public static bool IsEasyAuthEnabled(this IEnvironment environment)
        {
            bool.TryParse(environment.GetEnvironmentVariable(EasyAuthEnabled), out bool isEasyAuthEnabled);
            return isEasyAuthEnabled;
        }

        /// <summary>
        /// Returns true if any Functions AzureMonitor log categories are enabled.
        /// </summary>
        public static bool IsAzureMonitorEnabled(this IEnvironment environment)
        {
            string value = environment.GetEnvironmentVariable(AzureMonitorCategories);
            if (value == null)
            {
                return true;
            }
            string[] categories = value.Split(',');
            return categories.Contains(ScriptConstants.AzureMonitorTraceCategory);
        }

        public static bool IsRunningAsHostedSiteExtension(this IEnvironment environment)
        {
            if (environment.IsAppService())
            {
                string siteExtensionsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "SiteExtensions", "Functions");
                return (BaseDirectory ?? AppContext.BaseDirectory).StartsWith(siteExtensionsPath, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        public static bool IsRemoteDebuggingEnabled(this IEnvironment environment)
        {
            return !string.IsNullOrEmpty(environment.GetEnvironmentVariable(RemoteDebuggingPort));
        }

        public static bool ZipDeploymentAppSettingsExist(this IEnvironment environment)
        {
            return !string.IsNullOrEmpty(environment.GetEnvironmentVariable(AzureWebsiteZipDeployment)) ||
                   !string.IsNullOrEmpty(environment.GetEnvironmentVariable(AzureWebsiteAltZipDeployment)) ||
                   !string.IsNullOrEmpty(environment.GetEnvironmentVariable(AzureWebsiteRunFromPackage)) ||
                   !string.IsNullOrEmpty(environment.GetEnvironmentVariable(ScmRunFromPackage));
        }

        public static bool IsCoreTools(this IEnvironment environment)
        {
            return !string.IsNullOrEmpty(environment.GetEnvironmentVariable(CoreToolsEnvironment));
        }

        public static bool IsV2CompatibilityMode(this IEnvironment environment)
        {
            string compatModeString = environment.GetEnvironmentVariable(FunctionsV2CompatibilityModeKey);
            bool.TryParse(compatModeString, out bool isFunctionsV2CompatibilityMode);

            string extensionVersion = environment.GetEnvironmentVariable(FunctionsExtensionVersion);
            bool isV2ExtensionVersion = string.Compare(extensionVersion, "~2", CultureInfo.InvariantCulture, CompareOptions.OrdinalIgnoreCase) == 0;

            return isFunctionsV2CompatibilityMode || isV2ExtensionVersion;
        }

        public static bool IsV2CompatibileOnV3Extension(this IEnvironment environment)
        {
            string compatModeString = environment.GetEnvironmentVariable(FunctionsV2CompatibilityModeKey);
            bool.TryParse(compatModeString, out bool isFunctionsV2CompatibilityMode);

            string extensionVersion = environment.GetEnvironmentVariable(FunctionsExtensionVersion);
            bool isV3ExtensionVersion = string.Compare(extensionVersion, "~3", CultureInfo.InvariantCulture, CompareOptions.OrdinalIgnoreCase) == 0;

            return isFunctionsV2CompatibilityMode && isV3ExtensionVersion;
        }

        public static bool IsContainer(this IEnvironment environment)
        {
            var runningInContainer = environment.GetEnvironmentVariable(RunningInContainer);
            return !string.IsNullOrEmpty(runningInContainer)
                && bool.TryParse(runningInContainer, out bool runningInContainerValue)
                && runningInContainerValue;
        }

        public static bool IsPersistentFileSystemAvailable(this IEnvironment environment)
        {
            return environment.IsWindowsAzureManagedHosting()
                || environment.IsLinuxAppServiceWithPersistentFileSystem()
                || environment.IsCoreTools();
        }

        public static bool IsLinuxAppServiceWithPersistentFileSystem(this IEnvironment environment)
        {
            if (environment.IsLinuxAppService())
            {
                string storageConfig = environment.GetEnvironmentVariable(LinuxAzureAppServiceStorage);

                // AzureAppServiceStorage is enabled by default, So return if true it is not set
                if (string.IsNullOrEmpty(storageConfig))
                {
                    return true;
                }
                return bool.TryParse(storageConfig, out bool storageConfigValue) && storageConfigValue;
            }
            return false;
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in a Windows Consumption (dynamic)
        /// App Service environment.
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> if running in a Windows Consumption App Service app; otherwise, false.</returns>
        public static bool IsWindowsConsumption(this IEnvironment environment)
        {
            string value = environment.GetEnvironmentVariable(AzureWebsiteSku);
            return string.Equals(value, ScriptConstants.DynamicSku, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns true if the app is running on Virtual Machine Scale Sets (VMSS)
        /// </summary>
        public static bool IsVMSS(this IEnvironment environment)
        {
            string value = environment.GetEnvironmentVariable(EnvironmentSettingNames.RoleInstanceId);
            return value != null && value.IndexOf("HostRole", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Gets the number of effective cores taking into account SKU/environment restrictions.
        /// </summary>
        public static int GetEffectiveCoresCount(this IEnvironment environment)
        {
            // When not running on VMSS, the dynamic plan has some limits that mean that a given instance is using effectively a single core,
            // so we should not use Environment.Processor count in this case.
            var effectiveCores = (environment.IsWindowsConsumption() && !environment.IsVMSS()) ? 1 : Environment.ProcessorCount;
            return effectiveCores;
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in a Windows Elastic Premium
        /// App Service environment.
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> if running in a Windows Elastic Premium app; otherwise, false.</returns>
        public static bool IsWindowsElasticPremium(this IEnvironment environment)
        {
            string value = environment.GetEnvironmentVariable(AzureWebsiteSku);
            return string.Equals(value, ScriptConstants.ElasticPremiumSku, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDynamicSku(this IEnvironment environment)
        {
            return environment.IsConsumptionSku() || environment.IsWindowsElasticPremium();
        }

        public static bool IsConsumptionSku(this IEnvironment environment)
        {
            return environment.IsWindowsConsumption() || environment.IsLinuxConsumption();
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in an Azure Windows managed hosting environment
        /// (i.e. Windows Consumption or Windows Dedicated)
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> if running in a Windows Azure managed hosting environment; otherwise, false.</returns>
        public static bool IsWindowsAzureManagedHosting(this IEnvironment environment)
        {
            return environment.IsAppService() && RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in a Linux Consumption (dynamic)
        /// App Service environment.
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> if running in a Linux Consumption App Service app; otherwise, false.</returns>
        public static bool IsLinuxConsumption(this IEnvironment environment)
        {
            return !environment.IsAppService() && !string.IsNullOrEmpty(environment.GetEnvironmentVariable(ContainerName));
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in a Linux App Service
        /// environment (Dedicated Linux).
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> if running in a Linux Azure App Service; otherwise, false.</returns>
        public static bool IsLinuxAppService(this IEnvironment environment)
        {
            return environment.IsAppService() && !string.IsNullOrEmpty(environment.GetEnvironmentVariable(FunctionsLogsMountPath));
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in an Azure Linux managed hosting environment
        /// (i.e. Linux Consumption or Linux Dedicated)
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> if running in a Linux Azure managed hosting environment; otherwise, false.</returns>
        public static bool IsLinuxAzureManagedHosting(this IEnvironment environment)
        {
            return environment.IsLinuxConsumption() || environment.IsLinuxAppService();
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in App Service
        /// (Windows Consumption, Windows Dedicated or Linux Dedicated).
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> if running in a Azure App Service; otherwise, false.</returns>
        public static bool IsAppService(this IEnvironment environment)
        {
            return !string.IsNullOrEmpty(environment.GetEnvironmentVariable(AzureWebsiteInstanceId));
        }

        /// <summary>
        /// Gets a value indicating whether the application is running in Kubernetes App Service environment(K8SE)
        /// </summary>
        /// <param name="environment">The environment to verify</param>
        /// <returns><see cref="true"/> If running in a Kubernetes Azure App Service; otherwise, false.</returns>
        public static bool IsKubernetesManagedHosting(this IEnvironment environment)
        {
            return !string.IsNullOrEmpty(environment.GetEnvironmentVariable(KubernetesServiceHost))
            && !string.IsNullOrEmpty(environment.GetEnvironmentVariable(PodNamespace));
        }

        /// <summary>
        /// Gets a value that uniquely identifies the site and slot.
        /// </summary>
        public static string GetAzureWebsiteUniqueSlotName(this IEnvironment environment)
        {
            string name = environment.GetEnvironmentVariable(AzureWebsiteName);
            string slotName = environment.GetEnvironmentVariable(AzureWebsiteSlotName);

            if (!string.IsNullOrEmpty(slotName) &&
                !string.Equals(slotName, ScriptConstants.DefaultProductionSlotName, StringComparison.OrdinalIgnoreCase))
            {
                name += $"-{slotName}";
            }

            return name?.ToLowerInvariant();
        }

        /// <summary>
        /// Gets the computer name.
        /// </summary>
        public static string GetAntaresComputerName(this IEnvironment environment)
        {
            return environment.GetEnvironmentVariableOrDefault(AntaresComputerName, string.Empty);
        }

        /// <summary>
        /// Gets the computer name.
        /// </summary>
        public static bool IsLogicApp(this IEnvironment environment)
        {
            string appKind = environment.GetEnvironmentVariable(AppKind)?.ToLower();
            return !string.IsNullOrEmpty(appKind) && appKind.Contains(ScriptConstants.WorkFlowAppKind);
        }

        /// <summary>
        /// Gets the Antares version.
        /// </summary>
        public static string GetAntaresVersion(this IEnvironment environment)
        {
            if (environment.IsLinuxAzureManagedHosting())
            {
                return environment.GetEnvironmentVariableOrDefault(AntaresPlatformVersionLinux, string.Empty);
            }
            else
            {
                return environment.GetEnvironmentVariableOrDefault(AntaresPlatformVersionWindows, string.Empty);
            }
        }

        /// <summary>
        /// Gets the Instance id.
        /// </summary>
        public static string GetInstanceId(this IEnvironment environment)
        {
            if (environment.IsLinuxConsumption())
            {
                return environment.GetEnvironmentVariableOrDefault(ContainerName, string.Empty);
            }
            else
            {
                return environment.GetEnvironmentVariableOrDefault(AzureWebsiteInstanceId, string.Empty);
            }
        }

        /// <summary>
        /// Gets a the subscription Id of the current site.
        /// </summary>
        public static string GetSubscriptionId(this IEnvironment environment)
        {
            string ownerName = environment.GetEnvironmentVariable(AzureWebsiteOwnerName) ?? string.Empty;
            if (!string.IsNullOrEmpty(ownerName))
            {
                int idx = ownerName.IndexOf('+');
                if (idx > 0)
                {
                    return ownerName.Substring(0, idx);
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the name of the currently deployed site name.
        /// </summary>
        public static string GetRuntimeSiteName(this IEnvironment environment)
        {
            string runtimeSiteName = environment.GetEnvironmentVariable(AzureWebsiteRuntimeSiteName);
            return runtimeSiteName?.ToLowerInvariant();
        }

        /// <summary>
        /// Gets the name of the app's slot, if it exists.
        /// </summary>
        public static string GetSlotName(this IEnvironment environment)
        {
            string slotName = environment.GetEnvironmentVariable(AzureWebsiteSlotName);
            return slotName?.ToLowerInvariant();
        }

        /// <summary>
        /// Gets a value indicating whether it is safe to start specializing the host instance (e.g. file system is ready, etc.)
        /// </summary>
        public static bool IsContainerReady(this IEnvironment environment)
        {
            return !string.IsNullOrEmpty(environment.GetEnvironmentVariable(AzureWebsiteContainerReady));
        }

        public static string GetKubernetesApiServerUrl(this IEnvironment environment)
        {
            string host = environment.GetEnvironmentVariable(KubernetesServiceHost);
            string port = environment.GetEnvironmentVariable(KubernetesServiceHttpsPort);

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port))
            {
                throw new InvalidOperationException($"Both {KubernetesServiceHost} and {KubernetesServiceHttpsPort} are required for {nameof(GetKubernetesApiServerUrl)}.");
            }

            return $"https://{host}:{port}";
        }

        public static bool IsMountEnabled(this IEnvironment environment)
        {
            return string.Equals(environment.GetEnvironmentVariable(MountEnabled), "1")
                && !string.IsNullOrEmpty(environment.GetEnvironmentVariable(MeshInitURI));
        }

        public static bool IsMountDisabled(this IEnvironment environment)
        {
            return string.Equals(environment.GetEnvironmentVariable(MountEnabled), "0")
                || string.IsNullOrEmpty(environment.GetEnvironmentVariable(MeshInitURI));
        }

        public static CloudName GetCloudName(this IEnvironment environment)
        {
            var cloudName = environment.GetEnvironmentVariable(EnvironmentSettingNames.CloudName);
            if (Enum.TryParse(cloudName, true, out CloudName cloud))
            {
                return cloud;
            }

            return CloudName.Azure;
        }

        public static string GetStorageSuffix(this IEnvironment environment)
        {
            switch (GetCloudName(environment))
            {
                case CloudName.Azure:
                    return CloudConstants.AzureStorageSuffix;
                case CloudName.Blackforest:
                    return CloudConstants.BlackforestStorageSuffix;
                case CloudName.Fairfax:
                    return CloudConstants.FairfaxStorageSuffix;
                case CloudName.Mooncake:
                    return CloudConstants.MooncakeStorageSuffix;
                case CloudName.USNat:
                    return CloudConstants.USNatStorageSuffix;
                case CloudName.USSec:
                    return CloudConstants.USSecStorageSuffix;
                default:
                    return CloudConstants.AzureStorageSuffix;
            }
        }

        public static string GetVaultSuffix(this IEnvironment environment)
        {
            {
                switch (GetCloudName(environment))
                {
                    case CloudName.Azure:
                        return CloudConstants.AzureVaultSuffix;
                    case CloudName.Blackforest:
                        return CloudConstants.BlackforestVaultSuffix;
                    case CloudName.Fairfax:
                        return CloudConstants.FairfaxVaultSuffix;
                    case CloudName.Mooncake:
                        return CloudConstants.MooncakeVaultSuffix;
                    default:
                        return CloudConstants.AzureVaultSuffix;
                }
            }
        }

        public static string GetFunctionsWorkerRuntime(this IEnvironment environment)
        {
            return environment.GetEnvironmentVariableOrDefault(FunctionWorkerRuntime, string.Empty);
        }

        /// <summary>
        /// Gets a value indicating whether AzureFileShare should be mounted when specializing Linux Consumption workers.
        /// </summary>
        public static bool SupportsAzureFileShareMount(this IEnvironment environment)
        {
            return string.Equals(environment.GetFunctionsWorkerRuntime(), RpcWorkerConstants.PowerShellLanguageWorkerName,
                StringComparison.OrdinalIgnoreCase);
        }

        public static string GetHttpLeaderEndpoint(this IEnvironment environment)
        {
            return environment.GetEnvironmentVariableOrDefault(HttpLeaderEndpoint, string.Empty);
        }

        public static bool DrainOnApplicationStoppingEnabled(this IEnvironment environment)
        {
            return !string.IsNullOrEmpty(environment.GetEnvironmentVariable(KubernetesServiceHost)) ||
                (bool.TryParse(environment.GetEnvironmentVariable(DrainOnApplicationStopping), out bool v) && v);
        }

        /// <summary>
        /// Determine if shared memory transfer is enabled in this environment.
        /// <see cref="RpcWorkerConstants.FunctionsWorkerSharedMemoryDataTransferEnabledSettingName"/> must be set in environment variable (AppSetting).
        /// </summary>
        /// <returns><see cref="true"/> if shared memory data transfer is enabled, <see cref="false"/> otherwise.</returns>
        public static bool SupportsSharedMemoryTransfer(this IEnvironment environment)
        {
            // Check if the environment variable (AppSetting) has this feature enabled
            string envVal = environment.GetEnvironmentVariable(RpcWorkerConstants.FunctionsWorkerSharedMemoryDataTransferEnabledSettingName);
            if (string.IsNullOrEmpty(envVal))
            {
                return false;
            }

            if (bool.TryParse(envVal, out bool boolResult))
            {
                // Check if value was specified as a bool (true/false)
                return boolResult;
            }
            else if (int.TryParse(envVal, out int intResult) && intResult == 1)
            {
                // Check if value was specified as an int (1/0)
                return true;
            }

            return false;
        }
    }
}
