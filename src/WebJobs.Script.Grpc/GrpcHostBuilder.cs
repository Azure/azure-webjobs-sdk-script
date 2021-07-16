// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    internal static class GrpcHostBuilder
    {
        public static IHostBuilder CreateHostBuilder(IScriptEventManager scriptEventManager, int port) =>
            Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.ConfigureKestrel(options =>
                    {
                        options.Listen(IPAddress.Parse(WorkerConstants.HostName), port, listenOptions =>
                        {
                            listenOptions.Protocols = HttpProtocols.Http2;
                        });
                    });
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddSingleton(scriptEventManager);
                    });
                    webBuilder.UseStartup<Startup>();
                });
    }
}