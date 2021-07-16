// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    public class AspNetCoreGrpcServer : IRpcServer, IDisposable, IAsyncDisposable
    {
        private readonly IHostBuilder _grpcHostBuilder;
        private readonly int _port;
        private bool _disposed = false;
        private IHost _grpcHost;

        public AspNetCoreGrpcServer(IScriptEventManager scriptEventManager)
        {
            _port = WorkerUtilities.GetUnusedTcpPort();
            _grpcHostBuilder = GrpcHostBuilder.CreateHostBuilder(scriptEventManager, _port);
        }

        public Uri Uri => new Uri($"http://{WorkerConstants.HostName}:{_port}");

        public Task StartAsync()
        {
            _grpcHost = _grpcHostBuilder.Build();
            _grpcHost.Start();
            return Task.CompletedTask;
        }

        public Task ShutdownAsync() => _grpcHost.StopAsync();

        public Task KillAsync() => _grpcHost.StopAsync();

        protected async ValueTask DisposeAsync(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    await _grpcHost.StopAsync();
                    _grpcHost.Dispose();
                }
                _disposed = true;
            }
        }

        public ValueTask DisposeAsync()
        {
            return DisposeAsync(true);
        }

        public void Dispose()
        {
            DisposeAsync();
        }
    }
}
