// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Microsoft.Azure.WebJobs.Script.Workers.Rpc
{
    internal class RpcFunctionInvocationDispatcherLoadBalancer : IRpcFunctionInvocationDispatcherLoadBalancer
    {
        private int _counter = 0;

        internal int Counter => _counter;

        public IRpcWorkerChannel GetLanguageWorkerChannel(IEnumerable<IRpcWorkerChannel> rpcWorkerChannels)
        {
            if (rpcWorkerChannels == null)
            {
                throw new ArgumentNullException(nameof(rpcWorkerChannels));
            }

            var currentNumberOfWorkers = rpcWorkerChannels.Count();
            if (currentNumberOfWorkers == 0)
            {
                throw new InvalidOperationException($"Did not find any initialized language workers");
            }
            if (currentNumberOfWorkers == 1)
            {
                return rpcWorkerChannels.FirstOrDefault();
            }
            var workerIndex = Interlocked.Increment(ref _counter) % currentNumberOfWorkers;
            if (_counter < 0 || workerIndex < 0)
            {
                _counter = 0;
                workerIndex = 0;
            }
            return rpcWorkerChannels.ElementAt(workerIndex);
        }
    }
}
