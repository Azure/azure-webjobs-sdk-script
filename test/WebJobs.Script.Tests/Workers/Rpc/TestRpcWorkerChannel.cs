// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.ManagedDependencies;
using Microsoft.Azure.WebJobs.Script.Scale;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Logging;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers.Rpc
{
    public class TestRpcWorkerChannel : IRpcWorkerChannel, IDisposable
    {
        private string _workerId;
        private bool _isWebhostChannel;
        private bool _throwOnProcessStartUp;
        private IScriptEventManager _eventManager;
        private string _runtime;
        private ILogger _testLogger;
        private RpcWorkerChannelState _state;
        private List<Task> _executionContexts;
        private HashSet<string> _executingInvocations;
        private bool _isDisposed;

        public TestRpcWorkerChannel(string workerId, string runtime = null, IScriptEventManager eventManager = null, ILogger testLogger = null, bool isWebhostChannel = false, bool throwOnProcessStartUp = false)
        {
            _workerId = workerId;
            _isWebhostChannel = isWebhostChannel;
            _eventManager = eventManager;
            _runtime = runtime;
            _testLogger = testLogger;
            _throwOnProcessStartUp = throwOnProcessStartUp;
            _executionContexts = new List<Task>();
            _executingInvocations = new HashSet<string>();
            _isDisposed = false;
        }

        public string Id => _workerId;

        public bool IsDisposed => _isDisposed;

        public IDictionary<string, BufferBlock<ScriptInvocationContext>> FunctionInputBuffers => throw new NotImplementedException();

        public List<Task> ExecutionContexts => _executionContexts;

        public void Dispose()
        {
            _isDisposed = true;
        }

        public void SetupFunctionInvocationBuffers(IEnumerable<FunctionMetadata> functions)
        {
            _state = _state | RpcWorkerChannelState.InvocationBuffersInitialized;
            _testLogger.LogInformation("SetupFunctionInvocationBuffers called");
        }

        public void SendFunctionLoadRequests(ManagedDependencyOptions managedDependencies, TimeSpan? functionTimeout)
        {
            _testLogger.LogInformation("RegisterFunctions called");
        }

        public Task SendFunctionEnvironmentReloadRequest()
        {
            _testLogger.LogInformation("SendFunctionEnvironmentReloadRequest called");
            return Task.CompletedTask;
        }

        public void SendInvocationRequest(ScriptInvocationContext context)
        {
            _executingInvocations.Add(context.ExecutionContext.InvocationId.ToString());
            _testLogger.LogInformation("SendInvocationRequest called");
        }

        public void InvokeResponse(InvocationResponse invokeResponse)
        {
            _executingInvocations.Remove(invokeResponse.InvocationId);
        }

        public async Task StartWorkerProcessAsync()
        {
            // To verify FunctionDispatcher transistions
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            if (_throwOnProcessStartUp)
            {
                throw new ArgumentException("Process startup failed");
            }
            string workerVersion = Guid.NewGuid().ToString();
            IDictionary<string, string> workerCapabilities = new Dictionary<string, string>()
            {
                { "test", "testSupported" }
            };
            _state = _state | RpcWorkerChannelState.Initialized;
        }

        public void RaiseWorkerError()
        {
            Exception testEx = new Exception("Test Worker Error");
            _eventManager.Publish(new WorkerErrorEvent(_runtime, Id, testEx));
        }

        public void RaiseWorkerRestart()
        {
            _eventManager.Publish(new WorkerRestartEvent(_runtime, Id));
        }

        public void RaiseWorkerErrorWithCustomTimestamp(DateTime timestamp)
        {
            Exception testEx = new Exception("Test Worker Error");
            _eventManager.Publish(new WorkerErrorEvent(_runtime, Id, testEx, timestamp));
        }

        public async Task DrainInvocationsAsync()
        {
            await Task.WhenAll(ExecutionContexts);
            ExecutionContexts.Clear();
        }

        public Task<WorkerStatus> GetWorkerStatusAsync()
        {
            var status = new WorkerStatus
            {
                Latency = TimeSpan.FromMilliseconds(10)
            };
            return Task.FromResult(status);
        }

        public bool IsChannelReadyForInvocations()
        {
            return _state.HasFlag(RpcWorkerChannelState.InvocationBuffersInitialized | RpcWorkerChannelState.Initialized);
        }

        public bool IsExecutingInvocation(string invocationId)
        {
            return _executingInvocations.Contains(invocationId);
        }

        public bool TryFailExecutions(Exception exception)
        {
            // Executions are no longer executing
            _executingInvocations = new HashSet<string>();
            return true;
        }

        public Task<IEnumerable<FunctionMetadata>> WorkerGetFunctionMetadata()
        {
            throw new NotImplementedException();
        }
    }
}
