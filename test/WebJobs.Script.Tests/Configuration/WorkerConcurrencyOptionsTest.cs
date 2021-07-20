// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Configuration
{
    public class WorkerConcurrencyOptionsTest
    {
        [Fact]
        public void Constructor_Defaults()
        {
            var options = new WorkerConcurrencyOptions();

            Assert.Equal(false, options.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(10), options.AdjustmentPeriod);
            Assert.Equal(TimeSpan.FromSeconds(1), options.CheckInterval);
            Assert.Equal(10, options.HistorySize);
            Assert.Equal(1, options.HistoryThreshold);
            Assert.Equal(TimeSpan.FromSeconds(1F), options.LatencyThreshold);
            Assert.Equal(0, options.MaxWorkerCount);
        }

        [Fact]
        public void Format_ReturnsExpectedResult()
        {
            var options = new WorkerConcurrencyOptions
            {
                Enabled = true,
                LatencyThreshold = TimeSpan.FromSeconds(20),
                AdjustmentPeriod = TimeSpan.FromSeconds(20),
                CheckInterval = TimeSpan.FromSeconds(2),
                HistorySize = 20,
                HistoryThreshold = 0.5F,
                MaxWorkerCount = 20
            };

            string result = options.Format();
            string expected = @"{
  ""Enabled"": true,
  ""LatencyThreshold"": ""00:00:20"",
  ""AdjustmentPeriod"": ""00:00:20"",
  ""CheckInterval"": ""00:00:02"",
  ""HistorySize"": 20,
  ""HistoryThreshold"": 0.5,
  ""MaxWorkerCount"": 20
}";
            Assert.Equal(Regex.Replace(expected, @"\s+", string.Empty), Regex.Replace(result, @"\s+", string.Empty));
        }
    }
}
