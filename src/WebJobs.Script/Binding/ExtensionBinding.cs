// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Description;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Extensibility;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Script.Binding
{
    /// <summary>
    /// Wrapper used to adapt a <see cref="ScriptBinding"/> to the binding pipeline.
    /// </summary>
    public class ExtensionBinding : FunctionBinding
    {
        private ScriptBinding _binding;

        public ExtensionBinding(ScriptJobHostOptions config, ScriptBinding binding, BindingMetadata metadata) : base(config, metadata, binding.Context.Access)
        {
            _binding = binding;
            Attributes = _binding.GetAttributes();
        }

        internal Collection<Attribute> Attributes { get; private set; }

        public override Collection<CustomAttributeBuilder> GetCustomAttributes(Type parameterType)
        {
            Collection<CustomAttributeBuilder> attributeBuilders = new Collection<CustomAttributeBuilder>();
            foreach (var attribute in Attributes)
            {
                CustomAttributeBuilder builder = GetAttributeBuilder(attribute);
                attributeBuilders.Add(builder);
            }

            return attributeBuilders;
        }

        public override async Task BindAsync(BindingContext context)
        {
            context.Attributes = Attributes.ToArray();

            if (_binding.DefaultType == typeof(IAsyncCollector<byte[]>))
            {
                await BindAsyncCollectorAsync<byte[]>(context);
            }
            else if (_binding.DefaultType == typeof(IAsyncCollector<JObject>))
            {
                await BindAsyncCollectorAsync<JObject>(context);
            }
            else if (_binding.DefaultType == typeof(IAsyncCollector<string>))
            {
                await BindAsyncCollectorAsync<string>(context);
            }
            else if (_binding.DefaultType == typeof(Stream))
            {
                // TODO the sharedMemoryObject contains a "copy" of the value too - we should probably get rid of that
                // and make the extension just read directly from shared memory
                SharedMemoryObject sharedMemoryObject = context.Value as SharedMemoryObject;
                if (sharedMemoryObject != null)
                {
                    // TODO this first copy is needed because each invocation will have a separate (unqiue) mapName.
                    // Therefore, we first copy the attributes and then add a specific attribute (mapName) for that invocation.
                    // This cloning first may be slow so is there a better way?
                    var currentAttributes = Attributes.ToList();
                    currentAttributes.Add(new SharedMemoryAttribute(sharedMemoryObject.MemoryMapName, sharedMemoryObject.Count));
                    context.Attributes = currentAttributes.ToArray();
                }

                await BindStreamAsync(context, Access);
            }
            else if (_binding.DefaultType == typeof(JObject))
            {
                await BindJTokenAsync<JObject>(context, Access);
            }
            else if (_binding.DefaultType == typeof(JArray))
            {
                await BindJTokenAsync<JArray>(context, Access);
            }
            else if (_binding.DefaultType == typeof(string))
            {
                await BindStringAsync(context);
            }
            else
            {
                throw new NotSupportedException($"ScriptBinding type {_binding.DefaultType} is not supported");
            }
        }

        internal static CustomAttributeBuilder GetAttributeBuilder(Attribute attribute)
        {
            AttributeBuilderInfo constructionInfo = GetAttributeBuilderInfo(attribute);

            var namedProperties = constructionInfo.Properties.Keys.ToArray();
            var namedPropertyValues = constructionInfo.Properties.Values.ToArray();

            return new CustomAttributeBuilder(constructionInfo.Constructor, constructionInfo.ConstructorArgs, namedProperties, namedPropertyValues);
        }

        internal static AttributeBuilderInfo GetAttributeBuilderInfo(Attribute attribute)
        {
            IDictionary<string, object> attributeData = GetAttributeData(attribute);
            Dictionary<string, object> attributeDataCaseInsensitive = new Dictionary<string, object>(attributeData, StringComparer.OrdinalIgnoreCase);
            Type attributeType = attribute.GetType();

            // Pick the ctor with the longest parameter list where all parameters are matched.
            int longestMatch = -1;
            ConstructorInfo bestCtor = null;
            Dictionary<PropertyInfo, object> propertiesToSet = null;
            object[] constructorArgs = null;
            var ctors = attributeType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var currCtor in ctors)
            {
                var currCtorParams = currCtor.GetParameters();
                int len = currCtorParams.Length;
                object[] currConstructorArgs = new object[len];

                bool hasAllParameters = true;
                for (int i = 0; i < len; i++)
                {
                    var p = currCtorParams[i];
                    object value = null;
                    if (!attributeDataCaseInsensitive.TryGetValue(p.Name, out value) || value == null)
                    {
                        hasAllParameters = false;
                        break;
                    }

                    currConstructorArgs[i] = value;
                }

                if (hasAllParameters)
                {
                    if (len > longestMatch)
                    {
                        propertiesToSet = new Dictionary<PropertyInfo, object>();

                        // Set any remaining property values
                        foreach (var prop in attributeType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                        {
                            if (!prop.CanWrite || !prop.GetSetMethod(/*nonPublic*/ true).IsPublic ||
                                Nullable.GetUnderlyingType(prop.PropertyType) != null)
                            {
                                continue;
                            }

                            object objValue = null;
                            if (attributeDataCaseInsensitive.TryGetValue(prop.Name, out objValue))
                            {
                                propertiesToSet.Add(prop, objValue);
                            }
                        }

                        bestCtor = currCtor;
                        constructorArgs = currConstructorArgs;
                        longestMatch = len;
                    }
                }
            }

            if (bestCtor == null)
            {
                // error!!!
                throw new InvalidOperationException("Can't figure out which ctor to call.");
            }

            AttributeBuilderInfo info = new AttributeBuilderInfo
            {
                Constructor = bestCtor,
                ConstructorArgs = constructorArgs,
                Properties = propertiesToSet
            };

            return info;
        }

        internal static IDictionary<string, object> GetAttributeData(Attribute attribute)
        {
            Dictionary<string, object> attributeData = new Dictionary<string, object>();

            foreach (var property in attribute.GetType().GetProperties())
            {
                object value = property.GetValue(attribute);
                if (value != null)
                {
                    attributeData.Add(property.Name, value);
                }
            }

            return attributeData;
        }

        internal class AttributeBuilderInfo
        {
            public ConstructorInfo Constructor { get; set; }

            public object[] ConstructorArgs { get; set; }

            public IDictionary<PropertyInfo, object> Properties { get; set; }
        }
    }
}
