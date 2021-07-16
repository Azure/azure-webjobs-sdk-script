// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    public static class GrpcServiceCollectionsExtensions
    {
        public static IServiceCollection AddScriptGrpc(this IServiceCollection services)
        {
            services.AddSingleton<IRpcWorkerChannelFactory, GrpcWorkerChannelFactory>();
            services.AddSingleton<FunctionRpc.FunctionRpcBase, FunctionRpcService>();

            if (FeatureFlags.IsEnabled(ScriptConstants.FeatureFlagEnableAspNetCoreGrpc))
            {
                services.AddSingleton<IRpcServer, AspNetCoreGrpcServer>();
            }
            else
            {
                services.AddSingleton<IRpcServer, GrpcServer>();
            }

            return services;
        }
    }
}