﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Net.Http;
using System.Threading.Tasks;
using WebJobs.Script.ConsoleHost.Arm.Models;

namespace WebJobs.Script.ConsoleHost.Arm.Extensions
{
    public static class HttpResponseMessageExtensions
    {
        public async static Task<HttpResponseMessage> EnsureSuccessStatusCodeWithFullError(this HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                throw new FailedRequestException(response.RequestMessage.RequestUri, content, response.StatusCode, $"Response status code does not indicate success {response.RequestMessage.RequestUri} {content} {response.StatusCode}");
            }
            return response;
        }
    }
}