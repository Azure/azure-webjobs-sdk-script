// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Description;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    public interface IFunctionInvocationDispatcher : IDisposable
    {
        FunctionInvocationDispatcherState State { get; }

        int ErrorEventsThreshold { get; }

        Task InvokeAsync(ScriptInvocationContext invocationContext);

        Task InitializeAsync(IEnumerable<FunctionMetadata> functions, CancellationToken cancellationToken = default);

        Task<IDictionary<string, WorkerStatus>> GetWorkerStatusesAsync();

        Task ShutdownAsync();

        Task<bool> RestartWorkerWithInvocationIdAsync(string invocationId);

        Task StartWorkerChannel();
    }
}
