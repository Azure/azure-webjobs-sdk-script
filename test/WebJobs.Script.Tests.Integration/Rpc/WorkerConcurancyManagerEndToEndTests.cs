using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class WorkerConcurancyManagerEndToEndTests : IClassFixture<WorkerConcurancyManagerEndToEndTests.TestFixture>
    {
        public WorkerConcurancyManagerEndToEndTests(TestFixture fixture)
        {
            Fixture = fixture;
        }

        public TestFixture Fixture { get; set; }

        [Fact]
        public async Task WorkerStatus_NewWorkerAdded()
        {
            RpcFunctionInvocationDispatcher fd = null;
            IEnumerable<IRpcWorkerChannel> channels = null;
            // Latency > 1s
            TestScriptEventManager.WaitBeforePublish = TimeSpan.FromSeconds(2);
            await TestHelpers.Await(async () =>
            {
                fd = Fixture.JobHost.FunctionDispatcher as RpcFunctionInvocationDispatcher;
                channels = await fd.GetInitializedWorkerChannelsAsync();
                return channels.Count() == 2;
            }, pollingInterval: 1000, timeout: 120 * 1000);
        }

        public class TestFixture : ScriptHostEndToEndTestFixture
        {
            public TestFixture() : base(@"TestScripts\Node", "node", RpcWorkerConstants.NodeLanguageWorkerName,
                startHost: true, functions: new[] { "HttpTrigger" },
                concurrencyOptions: Options.Create(new WorkerConcurrencyOptions()
                {
                    Enabled = true,
                    MaxWorkerCount = 2,
                    AdjustmentPeriod = TimeSpan.Zero,
                    CheckInterval = TimeSpan.FromMilliseconds(1000)
                }))
            {
            }
        }

        internal class TestScriptEventManager : IScriptEventManager, IDisposable
        {
            private readonly IScriptEventManager _scriptEventManager;


            public TestScriptEventManager()
            {
                _scriptEventManager = new ScriptEventManager();
            }

            public static TimeSpan WaitBeforePublish;

            public async void Publish(ScriptEvent scriptEvent)
            {
                // Emulate long worker status latency
                await Task.Delay(WaitBeforePublish);
                try
                {
                    _scriptEventManager.Publish(scriptEvent);
                } 
                catch (ObjectDisposedException)
                {
                    // Do no throw ObjectDisposedException
                }
            }

            public IDisposable Subscribe(IObserver<ScriptEvent> observer)
            {
                return _scriptEventManager.Subscribe(observer);
            }

            public void Dispose() => ((IDisposable)_scriptEventManager).Dispose();
        }
    }
}
