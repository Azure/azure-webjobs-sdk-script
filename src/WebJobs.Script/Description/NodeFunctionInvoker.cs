﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using EdgeJs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script.Binding;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.Description
{
    // TODO: make this internal
    public class NodeFunctionInvoker : ScriptFunctionInvokerBase
    {
        private readonly Collection<FunctionBinding> _inputBindings;
        private readonly Collection<FunctionBinding> _outputBindings;
        private readonly string _script;
        private readonly DictionaryJsonConverter _dictionaryJsonConverter = new DictionaryJsonConverter();
        private readonly BindingMetadata _trigger;

        private Func<object, Task<object>> _scriptFunc;
        private Func<object, Task<object>> _clearRequireCache;
        private static string _functionTemplate;
        private static string _clearRequireCacheScript;

        static NodeFunctionInvoker()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (StreamReader reader = new StreamReader(assembly.GetManifestResourceStream("Microsoft.Azure.WebJobs.Script.functionTemplate.js")))
            {
                _functionTemplate = reader.ReadToEnd();
            }
            using (StreamReader reader = new StreamReader(assembly.GetManifestResourceStream("Microsoft.Azure.WebJobs.Script.clearRequireCache.js")))
            {
                _clearRequireCacheScript = reader.ReadToEnd();
            }
        }

        internal NodeFunctionInvoker(ScriptHost host, BindingMetadata trigger, FunctionMetadata functionMetadata, Collection<FunctionBinding> inputBindings, Collection<FunctionBinding> outputBindings)
            : base(host, functionMetadata)
        {
            _trigger = trigger;
            string scriptFilePath = functionMetadata.Source.Replace('\\', '/');
            _script = string.Format(CultureInfo.InvariantCulture, _functionTemplate, scriptFilePath);
            _inputBindings = inputBindings;
            _outputBindings = outputBindings;

            InitializeFileWatcherIfEnabled();
        }

        private Func<object, Task<object>> ScriptFunc
        {
            get
            {
                if (_scriptFunc == null)
                {
                    // We delay create the script function so any syntax errors in
                    // the function will be reported to the Dashboard as an invocation
                    // error rather than a host startup error
                    _scriptFunc = Edge.Func(_script);
                }
                return _scriptFunc;
            }
        }

        private Func<object, Task<object>> ClearRequireCacheFunc
        {
            get
            {
                if (_clearRequireCache == null)
                {
                    _clearRequireCache = Edge.Func(_clearRequireCacheScript);
                }
                return _clearRequireCache;
            }
        }

        public override async Task Invoke(object[] parameters)
        {
            object input = parameters[0];
            TraceWriter traceWriter = (TraceWriter)parameters[1];
            IBinder binder = (IBinder)parameters[2];
            ExecutionContext functionExecutionContext = (ExecutionContext)parameters[3];

            try
            {
                TraceWriter.Verbose(string.Format("Function started"));

                var scriptExecutionContext = CreateScriptExecutionContext(input, traceWriter, TraceWriter, binder, functionExecutionContext);

                // if there are any binding parameters in the output bindings,
                // parse the input as json to get the binding data
                Dictionary<string, string> bindingData = new Dictionary<string, string>();
                if (_outputBindings.Any(p => p.HasBindingParameters) ||
                    _inputBindings.Any(p => p.HasBindingParameters))
                {
                    bindingData = GetBindingData(input);
                }
                bindingData["InvocationId"] = functionExecutionContext.InvocationId.ToString();

                await ProcessInputBindingsAsync(binder, scriptExecutionContext, bindingData);

                object functionResult = await ScriptFunc(scriptExecutionContext);

                await ProcessOutputBindingsAsync(_outputBindings, input, binder, bindingData, scriptExecutionContext, functionResult);

                TraceWriter.Verbose(string.Format("Function completed (Success)"));
            }
            catch (Exception ex)
            {
                TraceWriter.Error(ex.Message, ex);
                TraceWriter.Verbose(string.Format("Function completed (Failure)"));
                throw;
            }
        }

        private async Task ProcessInputBindingsAsync(IBinder binder, Dictionary<string, object> executionContext, Dictionary<string, string> bindingData)
        {
            var bindings = (Dictionary<string, object>)executionContext["bindings"];

            // create an ordered array of all inputs and add to
            // the execution context. These will be promoted to
            // positional parameters
            List<object> inputs = new List<object>();
            inputs.Add(bindings[_trigger.Name]);

            var nonTriggerInputBindings = _inputBindings.Where(p => !p.IsTrigger);
            foreach (var inputBinding in nonTriggerInputBindings)
            {
                string value = null;
                using (MemoryStream stream = new MemoryStream())
                {
                    BindingContext bindingContext = new BindingContext
                    {
                        Binder = binder,
                        BindingData = bindingData,
                        Value = stream
                    };
                    await inputBinding.BindAsync(bindingContext);

                    stream.Seek(0, SeekOrigin.Begin);
                    StreamReader sr = new StreamReader(stream);
                    value = sr.ReadToEnd();
                }

                bindings.Add(inputBinding.Name, value);

                inputs.Add(value);
            }

            executionContext["inputs"] = inputs;
        }

        private static async Task ProcessOutputBindingsAsync(Collection<FunctionBinding> outputBindings, object input, IBinder binder, 
            Dictionary<string, string> bindingData, Dictionary<string, object> scriptExecutionContext, object functionResult)
        {
            if (outputBindings == null)
            {
                return;
            }

            // if the function returned binding values via the function result,
            // apply them to context.bindings
            var contextBindings = (Dictionary<string, object>)scriptExecutionContext["bindings"];
            IDictionary<string, object> functionOutputs = functionResult as IDictionary<string, object>;
            if (functionOutputs != null)
            {
                foreach (var output in functionOutputs)
                {
                    contextBindings[output.Key] = output.Value;
                }
            }

            foreach (FunctionBinding binding in outputBindings)
            {
                // get the output value from the script
                object value = null;
                if (contextBindings.TryGetValue(binding.Name, out value))
                {
                    if (value.GetType() == typeof(ExpandoObject))
                    {
                        value = JsonConvert.SerializeObject(value);
                    }

                    byte[] bytes = null;
                    if (value.GetType() == typeof(string))
                    {
                        bytes = Encoding.UTF8.GetBytes((string)value);
                    }

                    using (MemoryStream ms = new MemoryStream(bytes))
                    {
                        BindingContext bindingContext = new BindingContext
                        {
                            Input = input,
                            Binder = binder,
                            BindingData = bindingData,
                            Value = ms
                        };
                        await binding.BindAsync(bindingContext);
                    }
                }
            }
        }

        protected override void OnScriptFileChanged(object sender, FileSystemEventArgs e)
        {
            if (_scriptFunc == null)
            {
                // we're not loaded yet, so nothing to reload
                return;
            }

            // The ScriptHost is already monitoring for changes to function.json, so we skip those
            string fileName = Path.GetFileName(e.Name);
            if (string.Compare(fileName, ScriptHost.FunctionConfigFileName, StringComparison.OrdinalIgnoreCase) != 0)
            {
                // one of the script files for this function changed
                // force a reload on next execution
                _scriptFunc = null;

                // clear the node module cache
                ClearRequireCacheFunc(null).Wait();

                TraceWriter.Verbose(string.Format(CultureInfo.InvariantCulture, "Script for function '{0}' changed. Reloading.", Metadata.Name));
            }
        }

        private Dictionary<string, object> CreateScriptExecutionContext(object input, TraceWriter traceWriter, TraceWriter fileTraceWriter, IBinder binder, ExecutionContext functionExecutionContext)
        {
            // create a TraceWriter wrapper that can be exposed to Node.js
            var log = (Func<object, Task<object>>)(p =>
            {
                string text = (string)p;
                traceWriter.Verbose(text);
                fileTraceWriter.Verbose(text);
                return Task.FromResult<object>(null);
            });

            var bindings = new Dictionary<string, object>();
            var bind = (Func<object, Task<object>>)(p =>
            {
                IDictionary<string, object> bindValues = (IDictionary<string, object>)p;
                foreach (var bindValue in bindValues)
                {
                    bindings[bindValue.Key] = bindValue.Value;
                }
                return Task.FromResult<object>(null);
            });

            var context = new Dictionary<string, object>()
            {
                { "invocationId", functionExecutionContext.InvocationId },
                { "log", log },
                { "bindings", bindings },
                { "bind", bind }
            };

            Type triggerParameterType = input.GetType();
            if (triggerParameterType == typeof(string))
            {
                // if the input is json, convert to a json object
                Dictionary<string, object> jsonObject;
                if (TryDeserializeJsonObject((string)input, out jsonObject))
                {
                    input = jsonObject;
                }
            }
            else if (triggerParameterType == typeof(HttpRequestMessage))
            {
                // convert the request to a json object
                HttpRequestMessage request = (HttpRequestMessage)input;
                var requestObject = CreateRequestObject(request);
                input = requestObject;

                // If this is a WebHook function, the input should be the
                // request body
                HttpTriggerBindingMetadata httpBinding = _trigger as HttpTriggerBindingMetadata;
                if (httpBinding != null &&
                    !string.IsNullOrEmpty(httpBinding.WebHookType))
                {
                    input = requestObject["body"];

                    // make the entire request object available as well
                    // this is symmetric with context.res which we also support
                    context["req"] = requestObject;
                }
            }
            else if (triggerParameterType == typeof(TimerInfo))
            {
                TimerInfo timerInfo = (TimerInfo)input;
                var inputValues = new Dictionary<string, object>()
                {
                    { "isPastDue", timerInfo.IsPastDue }
                };
                if (timerInfo.ScheduleStatus != null)
                {
                    inputValues["last"] = timerInfo.ScheduleStatus.Last.ToString("s", CultureInfo.InvariantCulture);
                    inputValues["next"] = timerInfo.ScheduleStatus.Next.ToString("s", CultureInfo.InvariantCulture);
                }
                input = inputValues;
            }

            bindings.Add(_trigger.Name, input);

            return context;
        }

        private Dictionary<string, object> CreateRequestObject(HttpRequestMessage request)
        {
            // TODO: need to provide access to remaining request properties
            Dictionary<string, object> inputDictionary = new Dictionary<string, object>();
            inputDictionary["originalUrl"] = request.RequestUri.ToString();
            inputDictionary["method"] = request.Method.ToString().ToUpperInvariant();
            inputDictionary["query"] = request.GetQueryNameValuePairs().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            Dictionary<string, string> headers = new Dictionary<string, string>();
            foreach (var header in request.Headers)
            {
                // since HTTP headers are case insensitive, we lower-case the keys
                // as does Node.js request object
                headers.Add(header.Key.ToLowerInvariant(), header.Value.First());
            }
            inputDictionary["headers"] = headers;

            // if the request includes a body, add it to the request object 
            if (request.Content != null && request.Content.Headers.ContentLength > 0)
            {
                string body = request.Content.ReadAsStringAsync().Result;
                MediaTypeHeaderValue contentType = request.Content.Headers.ContentType;
                Dictionary<string, object> jsonObject;
                if (contentType != null && contentType.MediaType == "application/json" &&
                    TryDeserializeJsonObject(body, out jsonObject))
                {
                    // if the content - type of the request is json, deserialize into an object
                    inputDictionary["body"] = jsonObject;
                }
                else
                {
                    inputDictionary["body"] = body;
                }
            }

            return inputDictionary;
        }

        private bool TryDeserializeJsonObject(string json, out Dictionary<string, object> result)
        {
            result = null;

            if (!IsJson(json))
            {
                return false;
            }

            try
            {
                result = JsonConvert.DeserializeObject<Dictionary<string, object>>(json, _dictionaryJsonConverter);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsJson(string input)
        {
            input = input.Trim();
            return (input.StartsWith("{", StringComparison.OrdinalIgnoreCase) && input.EndsWith("}", StringComparison.OrdinalIgnoreCase))
                || (input.StartsWith("[", StringComparison.OrdinalIgnoreCase) && input.EndsWith("]", StringComparison.OrdinalIgnoreCase));
        }
    }
}
