﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommandLine;
using Microsoft.Azure.WebJobs.Script;
using WebJobs.Script.ConsoleHost.Common;

namespace WebJobs.Script.ConsoleHost.Commands
{
    public class InitCommand : Command
    {
        [Option("vsc", DefaultValue = SourceControl.Git, HelpText = "")]
        public SourceControl SourceControl { get; set; }

        private readonly Dictionary<string, string> fileToContentMap = new Dictionary<string, string>
        {
            { ".gitignore",  @"
bin
obj
csx
.vs
edge
Publish

*.user
*.suo
*.cscfg
*.Cache

/packages
/TestResults

/tools/NuGet.exe
/App_Data
/secrets
/data
"},
            { ScriptConstants.HostMetadataFileName, $"{{\"id\":\"{Guid.NewGuid().ToString().Replace("-", "").ToLowerInvariant()}\"}}" }
        };


        public override async Task Run()
        {
            if (SourceControl != SourceControl.Git)
            {
                throw new Exception("Only Git is supported right now for vsc");
            }

            foreach (var pair in fileToContentMap)
            {
                TraceInfo($"Writing {pair.Key}");
                using (var writer = new StreamWriter(pair.Key))
                {
                    await writer.WriteAsync(pair.Value);
                }
            }

            var exe = new Executable("git", "init");
            await exe.RunAsync(TraceInfo, TraceInfo);
        }
    }
}
