// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.Workers
{
    internal class DefaultWorkerProcessFactory : IWorkerProcessFactory
    {
        private readonly IOptions<WorkerConcurrencyOptions> _concurrencyOptions;
        private readonly ILogger _logger;

        public DefaultWorkerProcessFactory(ILoggerFactory loggerFactory, IOptions<WorkerConcurrencyOptions> concurrencyOptions)
        {
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }
            _logger = loggerFactory.CreateLogger<DefaultWorkerProcessFactory>();
            _concurrencyOptions = concurrencyOptions ?? throw new ArgumentNullException(nameof(concurrencyOptions));
        }

        public virtual Process CreateWorkerProcess(WorkerContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (context.Arguments == null)
            {
                throw new ArgumentNullException(nameof(context.Arguments));
            }
            if (context.Arguments.ExecutablePath == null)
            {
                throw new ArgumentNullException(nameof(context.Arguments.ExecutablePath));
            }
            var startInfo = new ProcessStartInfo(context.Arguments.ExecutablePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                ErrorDialog = false,
                WorkingDirectory = context.WorkingDirectory,
                Arguments = GetArguments(context),
            };

            ApplyWorkerConcurrencyLimits(startInfo);

            var processEnvVariables = context.EnvironmentVariables;
            if (processEnvVariables != null && processEnvVariables.Any())
            {
                foreach (var envVar in processEnvVariables)
                {
                    startInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
                    startInfo.Arguments = startInfo.Arguments.Replace($"%{envVar.Key}%", envVar.Value);
                }
                startInfo.Arguments = SanitizeExpandedArgument(startInfo.Arguments);
            }
            return new Process { StartInfo = startInfo };
        }

        private StringBuilder MergeArguments(StringBuilder builder, string arg)
        {
            string expandedArg = Environment.ExpandEnvironmentVariables(arg);
            return builder.AppendFormat(" {0}", expandedArg);
        }

        public string GetArguments(WorkerContext context)
        {
            var argumentsBuilder = context.Arguments.ExecutableArguments.Aggregate(new StringBuilder(), MergeArguments);
            if (!string.IsNullOrEmpty(context.Arguments.WorkerPath))
            {
                argumentsBuilder.AppendFormat(" \"{0}\"", context.Arguments.WorkerPath);
            }
            context.Arguments.WorkerArguments.Aggregate(argumentsBuilder, MergeArguments);
            argumentsBuilder.Append(context.GetFormattedArguments());
            return argumentsBuilder.ToString();
        }

        internal string SanitizeExpandedArgument(string envExpandedString)
        {
            var regex = new Regex(@"%(.+?)%");
            var matches = regex.Matches(envExpandedString);
            foreach (Match match in matches)
            {
                _logger.LogDebug($"Environment variable:{match.Value} is not set");
                envExpandedString = envExpandedString.Replace(match.Value, string.Empty);
            }
            return envExpandedString;
        }

        internal void ApplyWorkerConcurrencyLimits(ProcessStartInfo startInfo)
        {
            if (_concurrencyOptions.Value.Enabled)
            {
                // Remove concurrency limits for Python and Powershell language workers
                string functionWorkerRuntime = startInfo.EnvironmentVariables[RpcWorkerConstants.FunctionWorkerRuntimeSettingName];
                if (functionWorkerRuntime == RpcWorkerConstants.PythonLanguageWorkerName)
                {
                    startInfo.EnvironmentVariables[RpcWorkerConstants.PythonTreadpoolThreadCount] = RpcWorkerConstants.DefaultConcurrencyPython;
                }
                if (functionWorkerRuntime == RpcWorkerConstants.PowerShellLanguageWorkerName)
                {
                    startInfo.EnvironmentVariables[RpcWorkerConstants.PSWorkerInProcConcurrencyUpperBound] = RpcWorkerConstants.DefaultConcurrencyPS;
                }
            }
        }
    }
}
