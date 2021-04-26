// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Dynamic;
using System.IO;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Description;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Extensibility;
using Microsoft.Azure.WebJobs.Script.Workers.FunctionDataCache;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script.Binding
{
    public abstract class FunctionBinding
    {
        private readonly ScriptJobHostOptions _options;

        protected FunctionBinding(ScriptJobHostOptions options, BindingMetadata metadata, FileAccess access)
        {
            _options = options;
            Access = access;
            Metadata = metadata;
        }

        public BindingMetadata Metadata { get; private set; }

        public FileAccess Access { get; private set; }

        public abstract Task BindAsync(BindingContext context);

        public abstract Collection<CustomAttributeBuilder> GetCustomAttributes(Type parameterType);

        internal static Collection<FunctionBinding> GetBindings(ScriptJobHostOptions config, IEnumerable<IScriptBindingProvider> bindingProviders,
            IEnumerable<BindingMetadata> bindingMetadataCollection, FileAccess fileAccess)
        {
            Collection<FunctionBinding> bindings = new Collection<FunctionBinding>();

            if (bindings != null)
            {
                foreach (var bindingMetadata in bindingMetadataCollection)
                {
                    string type = bindingMetadata.Type.ToLowerInvariant();
                    switch (type)
                    {
                        case "http":
                            if (fileAccess != FileAccess.Write)
                            {
                                throw new InvalidOperationException("Http binding can only be used for output.");
                            }
                            bindings.Add(new HttpBinding(config, bindingMetadata, FileAccess.Write));
                            break;
                        default:
                            FunctionBinding binding = null;
                            if (TryParseFunctionBinding(config, bindingProviders, bindingMetadata.Raw, out binding))
                            {
                                bindings.Add(binding);
                            }
                            break;
                    }
                }
            }

            return bindings;
        }

        private static bool TryParseFunctionBinding(ScriptJobHostOptions config, IEnumerable<IScriptBindingProvider> bindingProviders, JObject metadata, out FunctionBinding functionBinding)
        {
            functionBinding = null;

            ScriptBindingContext bindingContext = new ScriptBindingContext(metadata);
            ScriptBinding scriptBinding = null;
            foreach (var provider in bindingProviders)
            {
                if (provider.TryCreate(bindingContext, out scriptBinding))
                {
                    break;
                }
            }

            if (scriptBinding == null)
            {
                return false;
            }

            BindingMetadata bindingMetadata = BindingMetadata.Create(metadata);
            functionBinding = new ExtensionBinding(config, scriptBinding, bindingMetadata);

            return true;
        }

        internal static IEnumerable ReadAsEnumerable(object value)
        {
            IEnumerable values = null;

            if (value is JArray jArray)
            {
                return jArray;
            }

            if (value is Stream)
            {
                // first deserialize the stream as a string
                ConvertStreamToValue((Stream)value, DataType.String, ref value);
            }

            string stringValue = value as string;
            if (Utility.IsJson(stringValue))
            {
                // json values can either be singleton objects,
                // or arrays of objects/values
                JToken token = JToken.Parse(stringValue);
                if (token.Type != JTokenType.Array)
                {
                    // not an array so create a new array and add
                    // the singleton
                    values = new JArray() { token };
                }
                else
                {
                    values = (JArray)token;
                }
            }
            else if (value is Array && !(value is byte[]))
            {
                values = (IEnumerable)value;
            }
            else
            {
                // not a collection, so just add the singleton value
                values = new object[] { value };
            }

            return values;
        }

        internal static async Task BindAsyncCollectorAsync<T>(BindingContext context)
        {
            IAsyncCollector<T> collector = await context.Binder.BindAsync<IAsyncCollector<T>>(context.Attributes);

            IEnumerable values = ReadAsEnumerable(context.Value);

            // convert values as necessary and add to the collector
            foreach (var value in values)
            {
                object converted = null;
                if (typeof(T) == typeof(string))
                {
                    if (value is ExpandoObject)
                    {
                        converted = Utility.ToJson((ExpandoObject)value, Formatting.None);
                    }
                    else
                    {
                        converted = value.ToString();
                    }
                }
                else if (typeof(T) == typeof(JObject))
                {
                    if (value is JObject)
                    {
                        converted = (JObject)value;
                    }
                    else if (value is ExpandoObject)
                    {
                        converted = Utility.ToJObject((ExpandoObject)value);
                    }
                }
                else if (typeof(T) == typeof(byte[]))
                {
                    byte[] bytes = value as byte[];
                    if (bytes == null)
                    {
                        string stringValue = null;
                        if (value is ExpandoObject)
                        {
                            stringValue = Utility.ToJson((ExpandoObject)value, Formatting.None);
                        }
                        else
                        {
                            stringValue = value.ToString();
                        }

                        bytes = Encoding.UTF8.GetBytes(stringValue);
                    }
                    converted = bytes;
                }
                else
                {
                    throw new ArgumentException("Unsupported collection type.");
                }

                await collector.AddAsync((T)converted);
            }
        }

        internal static async Task BindJTokenAsync<T>(BindingContext context, FileAccess access)
        {
            var result = await context.Binder.BindAsync<T>(context.Attributes);

            if (access == FileAccess.Read)
            {
                if (context.DataType == DataType.Stream)
                {
                    ConvertValueToStream(result, (Stream)context.Value);
                }

                context.Value = result;
            }
        }

        internal static async Task BindStringAsync(BindingContext context)
        {
            string str = await context.Binder.BindAsync<string>(context.Attributes);
            context.Value = str;
        }

        internal static async Task BindStreamAsync(BindingContext context, FileAccess access)
        {
            Stream stream = await context.Binder.BindAsync<Stream>(context.Attributes);

            if (access == FileAccess.Write)
            {
                ConvertValueToStream(context.Value, stream);
            }
            else
            {
                // Read the value into the context Value converting based on data type
                object converted = context.Value;
                ConvertStreamToValue(stream, context.DataType, ref converted);
                context.Value = converted;
            }
        }

        public static void ConvertValueToStream(object value, Stream stream)
        {
            // If the object is wrapped as a SharedMemoryObject, extract the inner value and
            // use that value and its corresponding type for further conversion.
            SharedMemoryObject sharedMemoryObject = value as SharedMemoryObject;
            if (sharedMemoryObject != null)
            {
                value = sharedMemoryObject.Content;
            }

            Stream valueStream = value as Stream;
            if (valueStream == null)
            {
                // Convert the value to bytes and write it
                // to the stream
                byte[] bytes = null;
                Type type = value.GetType();

                if (type == typeof(byte[]))
                {
                    bytes = (byte[])value;
                }
                else if (type == typeof(string))
                {
                    bytes = Encoding.UTF8.GetBytes((string)value);
                }
                else if (type == typeof(int))
                {
                    bytes = BitConverter.GetBytes((int)value);
                }
                else if (type == typeof(long))
                {
                    int val = unchecked((int)((long)value));
                    bytes = BitConverter.GetBytes(val);
                }
                else if (type == typeof(bool))
                {
                    bytes = BitConverter.GetBytes((bool)value);
                }
                else if (type == typeof(double))
                {
                    bytes = BitConverter.GetBytes((double)value);
                }
                else if (value is JToken)
                {
                    JToken jToken = (JToken)value;
                    string json = jToken.ToString(Formatting.None);
                    bytes = Encoding.UTF8.GetBytes(json);
                }
                else if (value is ExpandoObject)
                {
                    string json = Utility.ToJson((ExpandoObject)value, Formatting.None);
                    bytes = Encoding.UTF8.GetBytes(json);
                }

                using (valueStream = new MemoryStream(bytes))
                {
                    valueStream.CopyTo(stream);
                }
            }
            else
            {
                // value is already a stream, so copy it directly
                valueStream.CopyTo(stream);
            }
        }

        public static void ConvertStreamToValue(Stream stream, DataType dataType, ref object converted)
        {
            if (stream == null)
            {
                return;
            }

            switch (dataType)
            {
                case DataType.String:
                    using (StreamReader sr = new StreamReader(stream))
                    {
                        converted = sr.ReadToEnd();
                    }
                    break;
                case DataType.Binary:
                    if (stream is FunctionDataCacheStream)
                    {
                        // TODO explain what is happening here
                        FunctionDataCacheStream cacheStream = stream as FunctionDataCacheStream;
                        converted = cacheStream.SharedMemoryMetadata;
                    }
                    else if (stream is CacheableObjectStream)
                    {
                        // TODO explain what is happening here, delay the reading to the shared memory manager
                        CacheableObjectStream cachableObjStream = stream as CacheableObjectStream;
                        converted = cachableObjStream;
                    }
                    else
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            converted = ms.ToArray();
                        }
                    }
                    break;
                case DataType.Stream:
                    // when the target value is a Stream, we copy the value
                    // into the Stream passed in
                    if (converted != null)
                    {
                        Stream targetStream = converted as Stream;
                        stream.CopyTo(targetStream);
                    }
                    break;
            }
        }
    }
}
