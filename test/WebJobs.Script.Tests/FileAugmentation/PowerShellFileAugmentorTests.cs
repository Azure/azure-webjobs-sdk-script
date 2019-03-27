﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.FileAugmentation.PowerShell;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.FileAugmentation
{
    public class PowerShellFileAugmentorTests
    {
        private readonly string _scriptRootPath;

        public PowerShellFileAugmentorTests()
        {
            _scriptRootPath = Path.GetTempPath();
        }

        [Fact]
        public async Task AugmentFiles_Test()
        {
            File.Delete(Path.Combine(_scriptRootPath, "host.json"));
            File.Delete(Path.Combine(_scriptRootPath, "requirements.psd1"));
            File.Delete(Path.Combine(_scriptRootPath, "profile.ps1"));
            var powerShellFileAugmentor = new PowerShellFileAugmentor();
            await powerShellFileAugmentor.AugmentFiles(_scriptRootPath);
            File.Exists(Path.Combine(_scriptRootPath, "host.json"));
            File.Exists(Path.Combine(_scriptRootPath, "requirements.psd1"));
            File.Exists(Path.Combine(_scriptRootPath, "profile.ps1"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public async Task AugmentFiles_EmptyScriptRootPath_Test(string scriptRootPath)
        {
            var powerShellFileAugmentor = new PowerShellFileAugmentor();
            Exception ex = await Assert.ThrowsAsync<ArgumentException>(async () => await powerShellFileAugmentor.AugmentFiles(scriptRootPath));
            Assert.True(ex is ArgumentException);
        }
    }
}
