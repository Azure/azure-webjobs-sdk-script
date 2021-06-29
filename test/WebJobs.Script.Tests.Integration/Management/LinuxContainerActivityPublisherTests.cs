// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Microsoft.Azure.WebJobs.Script.WebHost.ContainerManagement;
using Microsoft.Azure.WebJobs.Script.WebHost.Management;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Integration.Management
{
    public class LinuxContainerActivityPublisherTests
    {
        private const int InitialFlushIntervalMs = 1;
        private const int FlushIntervalMs = 2;
        private const int DelayIntervalMs = 500;

        [Fact]
        public async Task PublishesFunctionExecutionActivity()
        {
            var activity = new ContainerFunctionExecutionActivity(DateTime.MinValue, "func-1",
                ExecutionStage.InProgress, "trigger-1", false);

            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(EnvironmentSettingNames.ContainerName, "Container-Name");

            var meshClient = new Mock<IMeshServiceClient>();
            meshClient.Setup(c => c.NotifyHealthEvent(ContainerHealthEventType.Informational, It.IsAny<Type>(),
                LinuxContainerActivityPublisher.SpecializationCompleteEvent)).Returns(Task.FromResult(true));
            meshClient.Setup(c =>
                c.PublishContainerActivity(
                    It.IsAny<IEnumerable<ContainerFunctionExecutionActivity>>())).Returns(Task.FromResult(true));

            var standbyOptions = new TestOptionsMonitor<StandbyOptions>(new StandbyOptions { InStandbyMode = false });

            using (var publisher = new LinuxContainerActivityPublisher(standbyOptions, meshClient.Object,
                environment, NullLogger<LinuxContainerActivityPublisher>.Instance, FlushIntervalMs, InitialFlushIntervalMs))
            {
                await publisher.StartAsync(CancellationToken.None);
                publisher.PublishFunctionExecutionActivity(activity);
                await Task.Delay(DelayIntervalMs);
                await publisher.StopAsync(CancellationToken.None);

                meshClient.Verify(c => c.NotifyHealthEvent(ContainerHealthEventType.Informational, It.IsAny<Type>(),
                    LinuxContainerActivityPublisher.SpecializationCompleteEvent), Times.Once);
                meshClient.Verify(
                    c => c.PublishContainerActivity(
                        It.Is<IEnumerable<ContainerFunctionExecutionActivity>>(e =>
                            MatchesFunctionActivities(e, activity))), Times.Once);
            }
        }

        [Fact]
        public async Task PublishesSpecializationCompleteEvent()
        {
            var activity = new ContainerFunctionExecutionActivity(DateTime.MinValue, "func-1",
                ExecutionStage.InProgress, "trigger-1", false);

            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(EnvironmentSettingNames.ContainerName, "Container-Name");

            var meshClient = new Mock<IMeshServiceClient>(MockBehavior.Strict);
            meshClient.Setup(c => c.NotifyHealthEvent(ContainerHealthEventType.Informational, It.IsAny<Type>(),
                LinuxContainerActivityPublisher.SpecializationCompleteEvent)).Returns(Task.FromResult(true));

            var standbyOptions = new TestOptionsMonitor<StandbyOptions>(new StandbyOptions { InStandbyMode = false });

            using (var publisher = new LinuxContainerActivityPublisher(standbyOptions, meshClient.Object,
                environment, NullLogger<LinuxContainerActivityPublisher>.Instance, 1000, InitialFlushIntervalMs))
            {
                await publisher.StartAsync(CancellationToken.None);
                publisher.PublishFunctionExecutionActivity(activity);
                await Task.Delay(100);
                await publisher.StopAsync(CancellationToken.None);

                meshClient.Verify(c => c.NotifyHealthEvent(ContainerHealthEventType.Informational, It.IsAny<Type>(),
                    LinuxContainerActivityPublisher.SpecializationCompleteEvent), Times.Once);

                // Since test is waiting for 100ms and Flush interval is 1000ms, there will be no PublishContainerActivity
                meshClient.Verify(
                    c => c.PublishContainerActivity(
                        It.Is<IEnumerable<ContainerFunctionExecutionActivity>>(e =>
                            MatchesFunctionActivities(e, activity))), Times.Never);
            }
        }

        [Fact]
        public async Task DoesNotPublishExecutionActivityInStandbyMode()
        {
            var activity = new ContainerFunctionExecutionActivity(DateTime.MinValue, "func-1",
                ExecutionStage.InProgress, "trigger-1", false);

            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            environment.SetEnvironmentVariable(EnvironmentSettingNames.ContainerName, "Container-Name");

            var meshClient = new Mock<IMeshServiceClient>();
            var standbyOptions = new TestOptionsMonitor<StandbyOptions>(new StandbyOptions { InStandbyMode = true });

            using (var publisher = new LinuxContainerActivityPublisher(standbyOptions, meshClient.Object,
                environment, NullLogger<LinuxContainerActivityPublisher>.Instance, FlushIntervalMs, InitialFlushIntervalMs))
            {
                await publisher.StartAsync(CancellationToken.None);
                publisher.PublishFunctionExecutionActivity(activity);
                await Task.Delay(DelayIntervalMs);
                await publisher.StopAsync(CancellationToken.None);
                meshClient.Verify(c =>
                    c.PublishContainerActivity(It.IsAny<IEnumerable<ContainerFunctionExecutionActivity>>()), Times.Never);
            }
        }

        [Fact]
        public void InitializingPublisherThrowsExceptionForNonLinuxConsumptionApps()
        {
            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            environment.SetEnvironmentVariable(EnvironmentSettingNames.ContainerName, string.Empty);

            var meshClient = new Mock<IMeshServiceClient>();
            var standbyOptions = new TestOptionsMonitor<StandbyOptions>(new StandbyOptions { InStandbyMode = false });
            var failsWithExpectedException = false;

            try
            {
                using (var publisher = new LinuxContainerActivityPublisher(standbyOptions, meshClient.Object,
                    environment, NullLogger<LinuxContainerActivityPublisher>.Instance, FlushIntervalMs, InitialFlushIntervalMs))
                {
                }
            }
            catch (Exception e)
            {
                failsWithExpectedException = e is NotSupportedException && string.Equals(e.Message,
                                                 $"{nameof(LinuxContainerActivityPublisher)} is available in Linux consumption environment only");
            }

            Assert.True(failsWithExpectedException);
        }

        [Fact]
        public async Task PublishesUniqueFunctionExecutionActivitiesOnly()
        {
            // activity1 and activity2 are duplicates. so only activity2 will be published
            var activity1 = new ContainerFunctionExecutionActivity(DateTime.MinValue, "func-1",
                ExecutionStage.InProgress, "trigger-1", false);
            var activity2 = new ContainerFunctionExecutionActivity(DateTime.MaxValue, "func-1",
                ExecutionStage.InProgress, "trigger-1", false);
            var activity3 = new ContainerFunctionExecutionActivity(DateTime.MaxValue, "func-1", ExecutionStage.Finished,
                "trigger-1", true);
            var activity4 = new ContainerFunctionExecutionActivity(DateTime.MaxValue, "func-1", ExecutionStage.Finished,
                "trigger-1", false);

            var environment = new TestEnvironment();
            environment.SetEnvironmentVariable(EnvironmentSettingNames.ContainerName, "Container-Name");

            var meshClient = new Mock<IMeshServiceClient>();

            var standbyOptions = new TestOptionsMonitor<StandbyOptions>(new StandbyOptions { InStandbyMode = false });

            using (var publisher = new LinuxContainerActivityPublisher(standbyOptions, meshClient.Object,
                environment, NullLogger<LinuxContainerActivityPublisher>.Instance, FlushIntervalMs, InitialFlushIntervalMs))
            {
                await publisher.StartAsync(CancellationToken.None);
                publisher.PublishFunctionExecutionActivity(activity1);
                publisher.PublishFunctionExecutionActivity(activity2);
                publisher.PublishFunctionExecutionActivity(activity3);
                publisher.PublishFunctionExecutionActivity(activity4);
                await Task.Delay(DelayIntervalMs);
                await publisher.StopAsync(CancellationToken.None);
                meshClient.Verify(
                    c => c.PublishContainerActivity(
                        It.Is<IEnumerable<ContainerFunctionExecutionActivity>>(e =>
                            MatchesFunctionActivities(e, activity2, activity3, activity4))), Times.Once);

            }
        }

        private static bool MatchesFunctionActivities(IEnumerable<ContainerFunctionExecutionActivity> activities,
            params ContainerFunctionExecutionActivity[] expectedActivities)
        {
            if (activities.Count() != expectedActivities.Length)
            {
                return false;
            }

            return expectedActivities.All(activities.Contains);
        }
    }
}
