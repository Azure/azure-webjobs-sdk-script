// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.ManagedDependencies;

namespace Microsoft.Azure.WebJobs.Script.Workers.Rpc
{
    public interface IRpcWorkerChannel : IWorkerChannel
    {
        IDictionary<string, BufferBlock<ScriptInvocationContext>> FunctionInputBuffers { get; }

        bool IsChannelReadyForInvocations();

        void SetupFunctionInvocationBuffers(IEnumerable<FunctionMetadata> functions);

        void SendFunctionLoadRequests(ManagedDependencyOptions managedDependencyOptions, TimeSpan? functionTimeout);

        Task SendFunctionEnvironmentReloadRequest();

        Task StartWorkerProcessAsync();

        Task DrainInvocationsAsync();

        bool IsExecutingInvocation(string invocationId);

        bool TryFailExecutions(Exception workerException);
    }
}
