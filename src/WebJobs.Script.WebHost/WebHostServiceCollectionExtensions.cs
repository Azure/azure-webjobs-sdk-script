// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.IO.Abstractions;
using System.Net.Http;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Grpc;
using Microsoft.Azure.WebJobs.Script.Middleware;
using Microsoft.Azure.WebJobs.Script.WebHost.Configuration;
using Microsoft.Azure.WebJobs.Script.WebHost.ContainerManagement;
using Microsoft.Azure.WebJobs.Script.WebHost.DependencyInjection;
using Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics;
using Microsoft.Azure.WebJobs.Script.WebHost.Filters;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Azure.WebJobs.Script.WebHost.Management.LinuxSpecialization;
using Microsoft.Azure.WebJobs.Script.WebHost.Metrics;
using Microsoft.Azure.WebJobs.Script.WebHost.Middleware;
using Microsoft.Azure.WebJobs.Script.WebHost.Security.Authorization;
using Microsoft.Azure.WebJobs.Script.WebHost.Security.Authorization.Policies;
using Microsoft.Azure.WebJobs.Script.WebHost.Standby;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.WebHost
{
    public static class WebHostServiceCollectionExtensions
    {
        public static IServiceCollection AddWebJobsScriptHostRouting(this IServiceCollection services)
        {
            // Add our script route handler
            services.TryAddSingleton<IWebJobsRouteHandler, ScriptRouteHandler>();

            return services.AddHttpBindingRouting();
        }

        public static IServiceCollection AddWebJobsScriptHostAuthentication(this IServiceCollection services)
        {
            services.AddAuthentication()
                .AddArmToken()
                .AddScriptAuthLevel()
                .AddScriptJwtBearer();

            return services;
        }

        public static IServiceCollection AddWebJobsScriptHostAuthorization(this IServiceCollection services)
        {
            services.AddAuthorization(o =>
            {
                o.AddScriptPolicies();
            });

            services.AddSingleton<IAuthorizationHandler, AuthLevelAuthorizationHandler>();
            services.AddSingleton<IAuthorizationHandler, NamedAuthLevelAuthorizationHandler>();
            return services.AddSingleton<IAuthorizationHandler, FunctionAuthorizationHandler>();
        }

        public static void AddWebJobsScriptHost(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddHttpContextAccessor();
            services.AddWebJobsScriptHostRouting();

            services.AddMvc(o =>
            {
                o.EnableEndpointRouting = false;
                o.Filters.Add(new ArmExtensionResourceFilter());
            })
            .AddNewtonsoftJson()
            .AddXmlDataContractSerializerFormatters();

            // Standby services
            services.AddStandbyServices();

            services.AddSingleton<IScriptHostManager>(s => s.GetRequiredService<WebJobsScriptHostService>());
            services.AddSingleton<IScriptWebHostEnvironment, ScriptWebHostEnvironment>();
            services.TryAddSingleton<IStandbyManager, StandbyManager>();
            services.TryAddSingleton<IScriptHostBuilder, DefaultScriptHostBuilder>();

            // Linux container services
            services.AddLinuxContainerServices();

            // ScriptSettingsManager should be replaced. We're setting this here as a temporary step until
            // broader configuration changes are made:
            services.AddSingleton<ScriptSettingsManager>();
            services.AddSingleton<IEventGenerator>(p =>
            {
                var environment = p.GetService<IEnvironment>();
                if (environment.IsLinuxConsumption())
                {
                    return new LinuxContainerEventGenerator(environment);
                }
                else if (SystemEnvironment.Instance.IsLinuxAppService())
                {
                    var hostNameProvider = p.GetService<HostNameProvider>();
                    return new LinuxAppServiceEventGenerator(new LinuxAppServiceFileLoggerFactory(), hostNameProvider);
                }
                else if (environment.IsKubernetesManagedHosting())
                {
                    return new KubernetesEventGenerator();
                }
                else
                {
                    return new EtwEventGenerator();
                }
            });

            // Management services
            services.AddSingleton<IFunctionsSyncManager, FunctionsSyncManager>();
            services.AddSingleton<IFunctionMetadataManager, FunctionMetadataManager>();
            services.AddSingleton<IFunctionMetadataProvider, HostFunctionMetadataProvider>();
            services.AddSingleton<IWebFunctionsManager, WebFunctionsManager>();
            services.AddSingleton<IInstanceManager, InstanceManager>();
            services.AddSingleton(_ => new HttpClient());
            services.AddSingleton<StartupContextProvider>();
            services.AddSingleton<HostNameProvider>();
            services.AddSingleton<IFileSystem>(_ => FileUtility.Instance);
            services.AddTransient<VirtualFileSystem>();
            services.AddTransient<VirtualFileSystemMiddleware>();

            // Logging and diagnostics
            services.AddSingleton<IMetricsLogger, WebHostMetricsLogger>();

            // Secret management
            services.TryAddSingleton<ISecretManagerProvider, DefaultSecretManagerProvider>();

            // Shared memory data transfer
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                services.AddSingleton<IMemoryMappedFileAccessor, MemoryMappedFileAccessorWindows>();
            }
            else
            {
                services.AddSingleton<IMemoryMappedFileAccessor, MemoryMappedFileAccessorUnix>();
            }
            services.AddSingleton<ISharedMemoryManager, SharedMemoryManager>();

            // Grpc
            services.AddScriptGrpc();

            // Register common services with the WebHost
            // Language Worker Hosted Services need to be intialized before WebJobsScriptHostService
            ScriptHostBuilderExtensions.AddCommonServices(services);

            // Core script host services
            services.AddSingleton<WebJobsScriptHostService>();
            services.AddSingleton<IHostedService>(s => s.GetRequiredService<WebJobsScriptHostService>());

            // Handles shutdown of services that need to happen after StopAsync() of all services of type IHostedService are complete.
            // Order is important.
            // All other IHostedService injections need to go before this.
            services.AddSingleton<IHostedService, HostedServiceManager>();

            // Configuration
            services.ConfigureOptions<ScriptApplicationHostOptionsSetup>();
            services.ConfigureOptions<StandbyOptionsSetup>();
            services.ConfigureOptions<LanguageWorkerOptionsSetup>();
            services.ConfigureOptionsWithChangeTokenSource<AppServiceOptions, AppServiceOptionsSetup, SpecializationChangeTokenSource<AppServiceOptions>>();
            services.ConfigureOptionsWithChangeTokenSource<HttpBodyControlOptions, HttpBodyControlOptionsSetup, SpecializationChangeTokenSource<HttpBodyControlOptions>>();

            services.TryAddSingleton<IDependencyValidator, DependencyValidator>();
            services.TryAddSingleton<IJobHostMiddlewarePipeline>(s => DefaultMiddlewarePipeline.Empty);

            // Add AzureStorageProvider to WebHost (also needed for ScriptHost)
            services.AddAzureStorageProvider();
        }

        private static void AddStandbyServices(this IServiceCollection services)
        {
            services.AddSingleton<IOptionsChangeTokenSource<StandbyOptions>, StandbyChangeTokenSource>();

            // Core script host service
            services.AddSingleton<IHostedService>(p =>
            {
                var standbyOptions = p.GetService<IOptionsMonitor<StandbyOptions>>();
                if (standbyOptions.CurrentValue.InStandbyMode)
                {
                    var standbyManager = p.GetService<IStandbyManager>();
                    return new StandbyInitializationService(standbyManager);
                }

                return NullHostedService.Instance;
            });
        }

        private static void AddLinuxContainerServices(this IServiceCollection services)
        {
            services.AddSingleton<IHostedService>(s =>
            {
                var environment = s.GetService<IEnvironment>();
                if (environment.IsLinuxConsumption())
                {
                    var instanceManager = s.GetService<IInstanceManager>();
                    var logger = s.GetService<ILogger<LinuxContainerInitializationHostService>>();
                    var startupContextProvider = s.GetService<StartupContextProvider>();
                    return new LinuxContainerInitializationHostService(environment, instanceManager, logger, startupContextProvider);
                }

                return NullHostedService.Instance;
            });

            services.AddSingleton<IMetricsPublisher>(s =>
            {
                var environment = s.GetService<IEnvironment>();
                if (environment.IsLinuxMetricsPublishingEnabled())
                {
                    var logger = s.GetService<ILogger<LinuxContainerMetricsPublisher>>();
                    var standbyOptions = s.GetService<IOptionsMonitor<StandbyOptions>>();
                    var hostNameProvider = s.GetService<HostNameProvider>();
                    return new LinuxContainerMetricsPublisher(environment, standbyOptions, logger, hostNameProvider);
                }

                return NullMetricsPublisher.Instance;
            });

            services.AddSingleton<IMeshServiceClient>(s =>
            {
                var environment = s.GetService<IEnvironment>();
                if (environment.IsLinuxConsumption())
                {
                    var httpClient = s.GetService<HttpClient>();
                    var logger = s.GetService<ILogger<MeshServiceClient>>();
                    return new MeshServiceClient(httpClient, environment, logger);
                }

                return NullMeshServiceClient.Instance;
            });

            services.AddSingleton<LinuxContainerActivityPublisher>(s =>
            {
                var environment = s.GetService<IEnvironment>();
                if (environment.IsLinuxConsumption())
                {
                    var logger = s.GetService<ILogger<LinuxContainerActivityPublisher>>();
                    var meshInitServiceClient = s.GetService<IMeshServiceClient>();
                    var standbyOptions = s.GetService<IOptionsMonitor<StandbyOptions>>();
                    return new LinuxContainerActivityPublisher(standbyOptions, meshInitServiceClient, environment, logger);
                }

                return null;
            });

            services.AddSingleton<IHostedService>(s =>
            {
                var environment = s.GetService<IEnvironment>();
                if (environment.IsLinuxConsumption())
                {
                    return s.GetRequiredService<LinuxContainerActivityPublisher>();
                }

                return NullHostedService.Instance;
            });

            services.AddSingleton<ILinuxContainerActivityPublisher>(s =>
            {
                var environment = s.GetService<IEnvironment>();
                if (environment.IsLinuxConsumption())
                {
                    return s.GetRequiredService<LinuxContainerActivityPublisher>();
                }

                return NullLinuxContainerActivityPublisher.Instance;
            });

            services.AddSingleton<IRunFromPackageHandler, RunFromPackageHandler>();
            services.AddSingleton<IUnZipHandler, UnZipHandler>();
            services.AddSingleton<IBashCommandHandler, BashCommandHandler>();
        }

        private static IServiceCollection ConfigureOptionsWithChangeTokenSource<TOptions, TOptionsSetup, TOptionsChangeTokenSource>(this IServiceCollection services)
            where TOptions : class
            where TOptionsSetup : class, IConfigureOptions<TOptions>
            where TOptionsChangeTokenSource : class, IOptionsChangeTokenSource<TOptions>
        {
            services.ConfigureOptions<TOptionsSetup>();
            services.AddSingleton<IOptionsChangeTokenSource<TOptions>, TOptionsChangeTokenSource>();

            return services;
        }
    }
}