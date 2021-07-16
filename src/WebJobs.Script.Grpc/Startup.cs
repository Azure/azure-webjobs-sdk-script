// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    internal class Startup
    {
        private const int MaxMessageLengthBytes = int.MaxValue;

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddGrpc(options =>
            {
                options.MaxReceiveMessageSize = MaxMessageLengthBytes;
                options.MaxSendMessageSize = MaxMessageLengthBytes;
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGrpcService<FunctionRpcService>();
            });
        }
    }
}