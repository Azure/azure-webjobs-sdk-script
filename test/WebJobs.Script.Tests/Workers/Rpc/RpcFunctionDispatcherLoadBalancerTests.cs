// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Workers.Rpc
{
    public class RpcFunctionDispatcherLoadBalancerTests
    {
        [Fact]
        public static void LoadBalancer_EqualDistribution_EvenNumberOfChannels()
        {
            List<IRpcWorkerChannel> results = new List<IRpcWorkerChannel>();
            int totalInvocations = 100;

            IEnumerable<IRpcWorkerChannel> workerChannels = new List<IRpcWorkerChannel>()
            {
                new TestRpcWorkerChannel("1"),
                new TestRpcWorkerChannel("2"),
                new TestRpcWorkerChannel("3"),
                new TestRpcWorkerChannel("4")
            };

            int expectedInvocationsPerChannel = totalInvocations / workerChannels.Count();

            IRpcFunctionInvocationDispatcherLoadBalancer loadBalancer = new RpcFunctionInvocationDispatcherLoadBalancer();
            for (int index = 0; index < 100; index++)
            {
                results.Add(loadBalancer.GetLanguageWorkerChannel(workerChannels));
            }
            var channelGroupsQuery = results.GroupBy(r => r.Id)
            .Select(g => new { Value = g.Key, Count = g.Count() });

            foreach (var channelGroup in channelGroupsQuery)
            {
                Assert.True(channelGroup.Count == expectedInvocationsPerChannel);
            }
        }

        [Fact]
        public static void LoadBalancer_OddNumberOfChannels()
        {
            List<IRpcWorkerChannel> results = new List<IRpcWorkerChannel>();
            int totalInvocations = 100;

            IEnumerable<IRpcWorkerChannel> workerChannels = new List<IRpcWorkerChannel>()
            {
                new TestRpcWorkerChannel("1"),
                new TestRpcWorkerChannel("2"),
                new TestRpcWorkerChannel("3")
            };

            int expectedInvocationsPerChannel = totalInvocations / workerChannels.Count();

            IRpcFunctionInvocationDispatcherLoadBalancer loadBalancer = new RpcFunctionInvocationDispatcherLoadBalancer();
            for (int index = 0; index < 100; index++)
            {
                results.Add(loadBalancer.GetLanguageWorkerChannel(workerChannels));
            }
            var channelGroupsQuery = results.GroupBy(r => r.Id)
            .Select(g => new { Value = g.Key, Count = g.Count() });

            foreach (var channelGroup in channelGroupsQuery)
            {
                Assert.True(channelGroup.Count >= expectedInvocationsPerChannel);
            }
        }

        [Fact]
        public static void LoadBalancer_SingleProcess_VerifyCounter()
        {
            List<IRpcWorkerChannel> results = new List<IRpcWorkerChannel>();

            IEnumerable<IRpcWorkerChannel> workerChannels = new List<IRpcWorkerChannel>()
            {
                new TestRpcWorkerChannel("1"),
            };

            IRpcFunctionInvocationDispatcherLoadBalancer loadBalancer = new RpcFunctionInvocationDispatcherLoadBalancer();
            RpcFunctionInvocationDispatcherLoadBalancer functionDispatcherLoadBalancer = loadBalancer as RpcFunctionInvocationDispatcherLoadBalancer;
            for (int index = 0; index < 10; index++)
            {
                loadBalancer.GetLanguageWorkerChannel(workerChannels);
                Assert.Equal(0, functionDispatcherLoadBalancer.Counter);
            }
        }

        [Fact]
        public static void LoadBalancer_Throws_InvalidOperationException_NoWorkerChannels()
        {
            List<IRpcWorkerChannel> results = new List<IRpcWorkerChannel>();
            IEnumerable<IRpcWorkerChannel> workerChannels = new List<IRpcWorkerChannel>();
            IRpcFunctionInvocationDispatcherLoadBalancer loadBalancer = new RpcFunctionInvocationDispatcherLoadBalancer();

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                loadBalancer.GetLanguageWorkerChannel(workerChannels);
            });

            Assert.Equal($"Did not find any initialized language workers", ex.Message);
        }
    }
}
