﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using static Microsoft.Azure.WebJobs.Script.EnvironmentSettingNames;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Middleware
{
    /// <summary>
    /// A middleware responsible for MaxRequestBodySize size configuration
    /// </summary>
    internal class HttpRequestBodySizeMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IEnvironment _environment;
        private RequestDelegate _invoke;
        private long _maxRequestBodySize;

        public HttpRequestBodySizeMiddleware(RequestDelegate next, IEnvironment environment)
        {
            _next = next;
            _environment = environment;
            _invoke = InvokeBeforeSpecialization;
        }

        // for testing
        internal RequestDelegate InnerInvoke { get => _invoke; }

        public Task Invoke(HttpContext context)
        {
            return _invoke(context);
        }

        internal Task InvokeAfterSpecialization(HttpContext httpContext)
        {
            httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = _maxRequestBodySize;
            return _next.Invoke(httpContext);
        }

        internal Task InvokeBeforeSpecialization(HttpContext context)
        {
            if (!_environment.IsPlaceholderModeEnabled())
            {
                string bodySizeLimit = _environment.GetEnvironmentVariable(FunctionsRequestBodySizeLimit);
                if (long.TryParse(bodySizeLimit, out _maxRequestBodySize))
                {
                    Interlocked.Exchange(ref _invoke, InvokeAfterSpecialization);
                    return Invoke(context);
                }
                else
                {
                    Interlocked.Exchange(ref _invoke, _next);
                }
            }
            return _next(context);
        }
    }
}
