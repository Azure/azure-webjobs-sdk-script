﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Handlers
{
    public class EnsureHostRunningHandler : DelegatingHandler
    {
        private readonly TimeSpan _hostTimeout = new TimeSpan(0, 0, 30);
        private readonly int _hostRunningPollIntervalMs = 500;
        private readonly HttpConfiguration _config;
 
        public EnsureHostRunningHandler(HttpConfiguration config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            _config = config;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var scriptHostManager = _config.DependencyResolver.GetService<WebScriptHostManager>();

            // some routes do not require the host to be running (most do)
            // in standby mode, we don't want to wait for host start
            bool bypassHostCheck = request.RequestUri.LocalPath.Trim('/').ToLowerInvariant().EndsWith("admin/host/status") ||
                ScriptHost.InStandbyMode;

            if (!scriptHostManager.Initialized)
            {
                scriptHostManager.Initialize();
            }

            if (!bypassHostCheck)
            {
                // If the host is not running, we'll wait a bit for it to fully
                // initialize. This might happen if http requests come in while the
                // host is starting up for the first time, or if it is restarting.
                TimeSpan timeWaited = TimeSpan.Zero;
                while (!scriptHostManager.IsRunning && (timeWaited < _hostTimeout))
                {
                    await Task.Delay(_hostRunningPollIntervalMs);
                    timeWaited += TimeSpan.FromMilliseconds(_hostRunningPollIntervalMs);
                }

                // if the host is not running after our wait time has expired
                // return a 503
                if (!scriptHostManager.IsRunning)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("Function host is not running.")
                    };
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}