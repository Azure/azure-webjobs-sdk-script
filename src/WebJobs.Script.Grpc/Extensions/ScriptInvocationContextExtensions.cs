// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs.Host.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Grpc.Extensions;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;
using RpcException = Microsoft.Azure.WebJobs.Script.Grpc.Messages.RpcException;

namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    internal static class ScriptInvocationContextExtensions
    {
        public static async Task<InvocationRequest> ToRpcInvocationRequest(this ScriptInvocationContext context, ILogger logger, GrpcCapabilities capabilities, bool isSharedMemoryDataTransferEnabled, ISharedMemoryManager sharedMemoryManager, IFunctionDataCache functionDataCache)
        {
            bool excludeHttpTriggerMetadata = !string.IsNullOrEmpty(capabilities.GetCapabilityState(RpcWorkerConstants.RpcHttpTriggerMetadataRemoved));

            var invocationRequest = new InvocationRequest
            {
                FunctionId = context.FunctionMetadata.GetFunctionId(),
                InvocationId = context.ExecutionContext.InvocationId.ToString(),
                TraceContext = GetRpcTraceContext(context.Traceparent, context.Tracestate, context.Attributes, logger),
            };

            SetRetryContext(context, invocationRequest);

            var rpcValueCache = new Dictionary<object, TypedData>();
            Dictionary<object, RpcSharedMemory> sharedMemValueCache = null;
            StringBuilder logBuilder = null;
            bool usedSharedMemory = false;

            if (isSharedMemoryDataTransferEnabled)
            {
                sharedMemValueCache = new Dictionary<object, RpcSharedMemory>();
                logBuilder = new StringBuilder();
            }

            foreach (var input in context.Inputs)
            {
                RpcSharedMemory sharedMemValue = null;
                ParameterBinding parameterBinding = null;
                if (isSharedMemoryDataTransferEnabled)
                {
                    // Try to transfer this data over shared memory instead of RPC
                    if (input.val == null || !sharedMemValueCache.TryGetValue(input.val, out sharedMemValue))
                    {
                        sharedMemValue = await input.val.ToRpcSharedMemoryAsync(logger, invocationRequest.InvocationId, sharedMemoryManager, functionDataCache);
                        if (input.val != null)
                        {
                            sharedMemValueCache.Add(input.val, sharedMemValue);
                        }
                    }
                }

                if (sharedMemValue != null)
                {
                    // Data was successfully transferred over shared memory; create a ParameterBinding accordingly
                    parameterBinding = new ParameterBinding
                    {
                        Name = input.name,
                        RpcSharedMemory = sharedMemValue
                    };

                    usedSharedMemory = true;
                    logBuilder.AppendFormat("{0}:{1},", input.name, sharedMemValue.Count);
                }
                else
                {
                    if (!TryConvertObjectIfNeeded(input.val, out object val))
                    {
                        // Conversion did not take place, keep the existing value as it is
                        val = input.val;
                    }

                    // Data was not transferred over shared memory (either disabled, type not supported or some error); resort to RPC
                    TypedData rpcValue = null;
                    if (val == null || !rpcValueCache.TryGetValue(val, out rpcValue))
                    {
                        rpcValue = await val.ToRpc(logger, capabilities);
                        if (input.val != null)
                        {
                            rpcValueCache.Add(val, rpcValue);
                        }
                    }

                    parameterBinding = new ParameterBinding
                    {
                        Name = input.name,
                        Data = rpcValue
                    };
                }

                invocationRequest.InputData.Add(parameterBinding);
            }

            foreach (var pair in context.BindingData)
            {
                if (ShouldSkipBindingData(pair, context, excludeHttpTriggerMetadata))
                {
                    continue;
                }

                if (!rpcValueCache.TryGetValue(pair.Value, out TypedData rpcValue))
                {
                    rpcValue = await pair.Value.ToRpc(logger, capabilities);
                    rpcValueCache.Add(pair.Value, rpcValue);
                }

                invocationRequest.TriggerMetadata.Add(pair.Key, rpcValue);
            }

            if (usedSharedMemory)
            {
                logger.LogDebug("Shared memory usage for request of invocation Id: {Id} is {SharedMemoryUsage}", invocationRequest.InvocationId, logBuilder.ToString());
            }

            return invocationRequest;
        }

        /// <summary>
        /// Determine whether we can omit the specified binding data for performance.
        /// </summary>
        private static bool ShouldSkipBindingData(KeyValuePair<string, object> bindingData, ScriptInvocationContext context, bool excludeHttpTriggerMetadata)
        {
            if (bindingData.Value == null)
            {
                return true;
            }

            // if this is an http request and the worker declares that it handles exclusion
            // of req/$request binding data members
            if (excludeHttpTriggerMetadata && bindingData.Value is HttpRequest)
            {
                return true;
            }

            if (bindingData.Key.Equals("sys", StringComparison.OrdinalIgnoreCase) &&
                bindingData.Value.GetType().Name.Equals("SystemBindingData", StringComparison.OrdinalIgnoreCase))
            {
                // The system binding data isn't RPC friendly. It's designed for in memory use in the binding
                // pipeline (e.g. sys.RandGuid, etc.)
                return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if the object being passed is converted into a form that can be sent to the worker.
        /// This is required in cases where an object conversion was being delayed for the <see cref="SharedMemoryManager"/> to handle,
        /// but for some reason (error, out of memory etc.) it was unable to do so.
        /// Therefore, we convert it to a form that can be sent without requiring shared memory.
        /// </summary>
        /// <param name="inputVal">Object to check if it is in a valid form.</param>
        /// <param name="convertedVal">Output object in a valid form if the input was invalid, <see cref="null"/> otherwise.</param>
        /// <returns><see cref="true"/> if a conversion took place, <see cref="false"/> otherwise.</returns>
        private static bool TryConvertObjectIfNeeded(object inputVal, out object convertedVal)
        {
            convertedVal = null;

            if (inputVal is CacheableObjectStream stream)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    byte[] value = ms.ToArray();
                    convertedVal = value;
                    return true;
                }
            }

            return false;
        }

        internal static RpcTraceContext GetRpcTraceContext(string activityId, string traceStateString, IEnumerable<KeyValuePair<string, string>> tags, ILogger logger)
        {
            RpcTraceContext traceContext = new RpcTraceContext
            {
                TraceParent = activityId ?? string.Empty,
                TraceState = traceStateString ?? string.Empty,
            };

            foreach (KeyValuePair<string, string> tag in tags ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                if (string.IsNullOrEmpty(tag.Value))
                {
                    logger?.LogDebug($"Excluding {tag.Key} from being added to TraceContext.Attributes since it's value is null/empty");
                }
                else
                {
                    if (traceContext.Attributes.ContainsKey(tag.Key))
                    {
                        logger?.LogWarning($"Overwriting '{tag.Key}' with existing value '{traceContext.Attributes[tag.Key]}' with '{tag.Value}'");
                    }
                    traceContext.Attributes[tag.Key] = tag.Value;
                }
            }

            return traceContext;
        }

        internal static void SetRetryContext(ScriptInvocationContext context, InvocationRequest invocationRequest)
        {
            if (context.ExecutionContext.RetryContext != null)
            {
                invocationRequest.RetryContext = new RetryContext()
                {
                    RetryCount = context.ExecutionContext.RetryContext.RetryCount,
                    MaxRetryCount = context.ExecutionContext.RetryContext.MaxRetryCount
                };
                // RetryContext.Exception should not be null, check just in case
                if (context.ExecutionContext.RetryContext.Exception != null)
                {
                    invocationRequest.RetryContext.Exception = new RpcException()
                    {
                        Message = ExceptionFormatter.GetFormattedException(context.ExecutionContext.RetryContext.Exception), // merge message from InnerException
                        StackTrace = context.ExecutionContext.RetryContext.Exception.StackTrace ?? string.Empty,
                        Source = context.ExecutionContext.RetryContext.Exception.Source ?? string.Empty
                    };
                }
            }
        }
    }
}
