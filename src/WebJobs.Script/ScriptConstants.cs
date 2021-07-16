// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Collections.Immutable;
using NuGet.Versioning;

namespace Microsoft.Azure.WebJobs.Script
{
    public static class ScriptConstants
    {
        public const string AzureFunctionsHttpResponseKey = "MS_AzureFunctionsHttpResponse";
        public const string AzureFunctionsHttpRequestKeyNameKey = "MS_AzureFunctionsKeyId";
        public const string AzureFunctionsHttpRequestAuthorizationDisabledKey = "MS_AzureFunctionsAuthorizationDisabled";
        public const string AzureFunctionsRequestIdKey = "MS_AzureFunctionsRequestID";
        public const string AzureFunctionsColdStartKey = "MS_AzureFunctionsColdStart";
        public const string AzureFunctionsRequestTimer = "MS_AzureFunctionsRequestTimer";
        public const string AzureFunctionsHttpRequestKey = "MS_AzureFunctionsHttpRequest";
        public const string AzureProxyFunctionExecutorKey = "MS_AzureProxyFunctionExecutor";
        public const string AzureFunctionsHostKey = "MS_AzureFunctionsHost";
        public const string AzureFunctionsNestedProxyCount = "MS_AzureFunctionsNestedProxyCount";
        public const string AzureFunctionsProxyResult = "MS_AzureFunctionsProxyResult";
        public const string AzureFunctionsDuplicateHttpHeadersKey = "MS_AzureFunctionsDuplicateHttpHeaders";
        public const string JobHostMiddlewarePipelineRequestDelegate = "MS_JobHostMiddlewarePipelineRequestDelegate";
        public const string HstsMiddlewareRequestDelegate = "MS_HstsMiddlewareRequestDelegate";
        public const string CorsMiddlewareRequestDelegate = "MS_CorsMiddlewareRequestDelegate";
        public const string EasyAuthMiddlewareRequestDelegate = "MS_EasyAuthMiddlewareRequestDelegate";

        public const string LegacyPlaceholderTemplateSiteName = "FunctionsPlaceholderTemplateSite";

        public const string LogPropertyPrimaryHostKey = "MS_PrimaryHost";
        public const string LogPropertySourceKey = "MS_Source";
        public const string LogPropertyFunctionNameKey = "MS_FunctionName";
        public const string LogPropertyEventNameKey = "MS_EventName";
        public const string LogPropertyEventDetailsKey = "MS_EventDetails";
        public const string LogPropertyIsUserLogKey = "MS_IsUserLog";
        public const string LogPropertyIsSystemLogKey = "MS_IsSystemLog";
        public const string LogPropertyFunctionInvocationIdKey = "MS_FunctionInvocationId";
        public const string LogPropertyHostInstanceIdKey = "HostInstanceId";
        public const string LogPropertyActivityIdKey = "MS_ActivityId";

        public const string TraceSourceSecretManagement = "SecretManagement";
        public const string TraceSourceHostAdmin = "HostAdmin";
        public const string TraceSourceFileWatcher = "FileWatcher";
        public const string TraceSourceHttpHandler = "HttpRequestTraceHandler";
        public const string TraceSourceHttpThrottleMiddleware = "HttpThrottleMiddleware";

        public const string LoggerHttpRequest = "MS_HttpRequest";

        public const string LogCategoryHostController = "Host.Controllers.Host";
        public const string LogCategoryFunctionsController = "Host.Controllers.Functions";
        public const string LogCategoryInstanceController = "Host.Controllers.Instance";
        public const string LogCategoryKeysController = "Host.Controllers.Keys";
        public const string LogCategoryHostGeneral = "Host.General";
        public const string LogCategoryHostMetrics = "Host.Metrics";
        public const string LogCategoryHost = "Host";
        public const string LogCategoryFunction = "Function";
        public const string LogCategoryWorker = "Worker";

        public const string SkipHostJsonConfigurationKey = "MS_SkipHostJsonConfiguration";
        public const string SkipHostInitializationKey = "MS_SkipHostInitialization";

        // Define all system parameters we inject with a prefix to avoid collisions
        // with user parameters
        public const string SystemTriggerParameterName = "_triggerValue";
        public const string SystemExecutionContextParameterName = "_context";
        public const string SystemLogParameterName = "_log";
        public const string SystemBinderParameterName = "_binder";
        public const string SystemReturnParameterBindingName = "$return";
        public const string SystemReturnParameterName = "_return";
        public const string SystemLoggerParameterName = "_logger";

        public const string DebugSentinelFileName = "debug_sentinel";
        public const string DiagnosticSentinelFileName = "diagnostic_sentinel";
        public const string HostMetadataFileName = "host.json";
        public const string FunctionMetadataFileName = "function.json";
        public const string AutorestGeenratedMetadataFileName = ".autorest_generated.json";
        public const string ProxyMetadataFileName = "proxies.json";
        public const string ExtensionsMetadataFileName = "extensions.json";
        public const string AppOfflineFileName = "app_offline.htm";
        public const string RunFromPackageFailedFileName = "FAILED TO INITIALIZE RUN FROM PACKAGE.txt";
        public const string ResourcePath = "Microsoft.Azure.WebJobs.Script.WebHost.Resources";

        public const string DefaultMasterKeyName = "master";
        public const string DefaultFunctionKeyName = "default";
        public const string ColdStartEventName = "ColdStart";

        public const string FunctionsUserAgent = "AzureFunctionsRuntime";
        public const string HttpScaleUserAgent = "HttpScaleManager";
        public const string HealthCheckQueryParam = "checkHealth";
        public const string ScaleControllerUserAgent = "ElasticScaleController";
        public const string AntaresDefaultHostNameHeader = "WAS-DEFAULT-HOSTNAME";
        public const string AntaresARMRequestTrackingIdHeader = "x-ms-arm-request-tracking-id";
        public const string AntaresARMExtensionsRouteHeader = "X-MS-VIA-EXTENSIONS-ROUTE";
        public const string AntaresClientAuthorizationSourceHeader = "X-MS-CLIENT-AUTHORIZATION-SOURCE";
        public const string AntaresLogIdHeaderName = "X-ARR-LOG-ID";
        public const string AntaresScaleOutHeaderName = "X-FUNCTION-SCALEOUT";
        public const string AntaresColdStartHeaderName = "X-MS-COLDSTART";
        public const string SiteTokenHeaderName = "x-ms-site-restricted-token";
        public const string EasyAuthIdentityHeader = "x-ms-client-principal";
        public const string DynamicSku = "Dynamic";
        public const string ElasticPremiumSku = "ElasticPremium";
        public const string DefaultProductionSlotName = "production";

        public const string AzureProxyFunctionLocalRedirectKey = "MS_ProxyLocalRedirectCount";
        public const int AzureProxyFunctionMaxLocalRedirects = 10;

        public const string FeatureFlagDisableShadowCopy = "DisableShadowCopy";
        public const string FeatureFlagsEnableDynamicExtensionLoading = "EnableDynamicExtensionLoading";
        public const string FeatureFlagEnableActionResultHandling = "EnableActionResultHandling";
        public const string FeatureFlagAllowSynchronousIO = "AllowSynchronousIO";
        public const string FeatureFlagRelaxedAssemblyUnification = "RelaxedAssemblyUnification";
        public const string FeatureFlagEnableEnhancedScopes = "EnableEnhancedScopes";
        public const string FeatureFlagDisableDevInDebug = "DisableDevModeInDebug";
        public const string FeatureFlagEnableDiagnosticEventLogging = "EnableDiagnosticEventLogging";
        public const string FeatureFlagDisableMergedWebHostScriptHostConfiguration = "DisableMergedConfiguration";
        public const string FeatureFlagEnableAspNetCoreGrpc = "EnableAspNetCoreGrpc";

        public const string AdminJwtValidAudienceFormat = "https://{0}.azurewebsites.net/azurefunctions";
        public const string AdminJwtValidIssuerFormat = "https://{0}.scm.azurewebsites.net";

        public const string AzureFunctionsSystemDirectoryName = ".azurefunctions";
        public const string HttpMethodConstraintName = "httpMethod";
        public const string Snapshot = "snapshot";
        public const string Runtime = "runtime";
        public const string NugetFallbackFolderRootName = "FuncNuGetFallback";
        public const string NugetXmlDocModeSettingName = "NUGET_XMLDOC_MODE";
        public const string NugetXmlDocSkipMode = "skip";

        public const string MediatypeOctetStream = "application/octet-stream";
        public const string MediatypeMutipartPrefix = "multipart/";

        public const int MaximumHostIdLength = 32;
        public const int DynamicSkuConnectionLimit = 50;

        /// <summary>
        /// This constant is also defined in Antares, where the limit is ultimately enforced
        /// for settriggers calls. If we raise that limit there, we should raise here as well.
        /// </summary>
        public const int MaxTriggersStringLength = 204800;
        public const int MaxTestDataInlineStringLength = 4 * 1024;

        public const string ExtensionsProjectFileName = "extensions.csproj";
        public const string MetadataGeneratorPackageId = "Microsoft.Azure.WebJobs.Script.ExtensionsMetadataGenerator";
        public const string MetadataGeneratorPackageVersion = "1.1.*";
        public const string PackageReferenceElementName = "PackageReference";
        public const string PackageReferenceVersionElementName = "Version";
        public const int HostTimeoutSeconds = 30;
        public const int HostPollingIntervalMilliseconds = 25;
        public const int MaximumSecretBackupCount = 10;

        public const string LinuxLogEventStreamName = "MS_FUNCTION_LOGS";
        public const string LinuxMetricEventStreamName = "MS_FUNCTION_METRICS";
        public const string LinuxFunctionDetailsEventStreamName = "MS_FUNCTION_DETAILS";
        public const string LinuxAzureMonitorEventStreamName = "MS_FUNCTION_AZURE_MONITOR_EVENT";
        public const string LinuxMSISpecializationStem = "/api/specialize?api-version=2017-09-01";

        public const string DurableTaskPropertyName = "durableTask";
        public const string DurableTaskHubName = "HubName";

        public const string AzureWebJobsHostsContainerName = "azure-webjobs-hosts";

        public const string DefaultExtensionBundleDirectory = "FuncExtensionBundles";
        public const string ExtensionBundleDirectory = "ExtensionBundles";
        public const string ExtensionBundleDefaultSourceUri = "https://functionscdn.azureedge.net/public";
        public const string ExtensionBundleMetadataFile = "bundle.json";
        public const string ExtensionBundleVersionIndexFile = "index.json";
        public const string ExtensionBundleBindingMetadataFile = "bindings.json";
        public const string ExtensionBundleTemplatesFile = "templates.json";
        public const string ExtensionBundleResourcesFile = "Resources.json";
        public const string DefaultExtensionBundleId = "Microsoft.Azure.Functions.ExtensionBundle";
        public const string WorkFlowExtensionBundleId = "Microsoft.Azure.Functions.ExtensionBundle.Workflows";
        public const string ExtensionBundleForAppServiceWindows = "win-any";
        public const string ExtensionBundleForAppServiceLinux = "linux-x64";
        public const string ExtensionBundleForNonAppServiceEnvironment = "any-any";
        public const string ExtensionBundleV3BinDirectoryName = "bin_v3";
        public const string Linux64BitRID = "linux-x64";
        public const string Windows64BitRID = "win-x64";
        public const string Windows32BitRID = "win-x86";
        public const string WorkFlowAppKind = "workflowapp";

        public const string AzureMonitorTraceCategory = "FunctionAppLogs";

        public const string KubernetesManagedAppName = "K8SE_APP_NAME";
        public const string KubernetesManagedAppNamespace = "K8SE_APP_NAMESPACE";
        public const string DiagnosticEventKey = "MS_DiagnosticEvent";
        public const string HelpLinkKey = "MS_HelpLink";
        public const string ErrorCodeKey = "MS_ErrorCode";

        public static readonly ImmutableArray<string> HttpMethods = ImmutableArray.Create("get", "post", "delete", "head", "patch", "put", "options");
        public static readonly ImmutableArray<string> AssemblyFileTypes = ImmutableArray.Create(".dll", ".exe");
        public static readonly string HostUserAgent = $"azure-functions-host/{ScriptHost.Version}";
        public static readonly NuGetVersion ExtensionBundleVersionTwo = new NuGetVersion("2.0.0");
        public static readonly long DefaultMaxRequestBodySize = 104857600;

        public static readonly ImmutableArray<string> SystemLogCategoryPrefixes = ImmutableArray.Create("Microsoft.Azure.WebJobs.", "Function.", "Worker.", "Host.");
    }
}
