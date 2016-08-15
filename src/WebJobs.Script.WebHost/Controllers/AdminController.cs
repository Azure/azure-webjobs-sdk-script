﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.WebHost.Filters;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Controllers
{
    /// <summary>
    /// Controller responsible for handling all administrative requests, for
    /// example enqueueing function invocations, etc.
    /// </summary>
    [AuthorizationLevel(AuthorizationLevel.Admin)]
    public class AdminController : ApiController
    {
        private readonly WebScriptHostManager _scriptHostManager;
        private readonly WebHostSettings _webHostSettings;

        public AdminController(WebScriptHostManager scriptHostManager, WebHostSettings webHostSettings)
        {
            _scriptHostManager = scriptHostManager;
            _webHostSettings = webHostSettings;
        }

        [HttpPost]
        [Route("admin/functions/{name}")]
        public async Task<HttpResponseMessage> Invoke(string name, [FromBody] FunctionInvocation invocation)
        {
            if (invocation == null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            FunctionDescriptor function = _scriptHostManager.Instance.Functions.FirstOrDefault(p => p.Name.ToLowerInvariant() == name.ToLowerInvariant());
            if (function == null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            ParameterDescriptor inputParameter = function.Parameters.First();
            Dictionary<string, object> arguments = new Dictionary<string, object>()
            {
                { inputParameter.Name, invocation.Input }
            };
            var runTask = Task.Run(() => _scriptHostManager.Instance.CallAsync(function.Name, arguments));

            if (invocation.WaitForCompletion)
            {
                try
                {
                    await runTask;
                    return Request.CreateResponse(HttpStatusCode.OK);
                }
                catch (Exception e)
                {
                    return Request.CreateErrorResponse(HttpStatusCode.InternalServerError, e);
                }
            }
            else
            {
                return Request.CreateResponse(HttpStatusCode.Accepted);
            }
        }

        [HttpGet]
        [Route("admin/functions/{name}/status")]
        public FunctionStatus GetFunctionStatus(string name)
        {
            Collection<string> functionErrors = null;
            FunctionDescriptor function = _scriptHostManager.Instance.Functions.FirstOrDefault(p => p.Name.ToLowerInvariant() == name.ToLowerInvariant());
            FunctionStatus status = new FunctionStatus
            {
                Metadata = function?.Metadata
            };

            // first see if the function has any errors
            if (_scriptHostManager.Instance.FunctionErrors.TryGetValue(name, out functionErrors))
            {
                status.Errors = functionErrors;
            }
            else
            {
                // if we don't have any errors registered, make sure the function exists
                // before returning empty errors
                if (function == null)
                {
                    throw new HttpResponseException(HttpStatusCode.NotFound);
                }
            }

            return status;
        }

        [HttpGet]
        [Route("admin/host/status")]
        public HostStatus GetHostStatus()
        {
            HostStatus status = new HostStatus
            {
                WebHostSettings = _webHostSettings,
                ProcessId = Process.GetCurrentProcess().Id,
                IsDebuggerAttached = Debugger.IsAttached
            };

            var lastError = _scriptHostManager.LastError;
            if (lastError != null)
            {
                status.Errors = new Collection<string>();
                status.Errors.Add(Utility.FlattenException(lastError));
            }

            return status;
        }

        [HttpPost]
        [Route("admin/host/debug")]
        public bool LaunchDebugger()
        {
            if (_webHostSettings.IsSelfHost)
            {
                return Debugger.Launch();
            }
            return false;
        }
    }
}
