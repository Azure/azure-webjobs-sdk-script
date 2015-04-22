﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Storage.Queue;

namespace Microsoft.Azure.WebJobs.Host.Queues.Bindings
{
    internal class ByteArrayArgumentBindingProvider : IQueueArgumentBindingProvider
    {
        private readonly IContextGetter<IMessageEnqueuedWatcher> _messageEnqueuedWatcherGetter;

        public ByteArrayArgumentBindingProvider(IContextGetter<IMessageEnqueuedWatcher> messageEnqueuedWatcherGetter)
        {
            if (messageEnqueuedWatcherGetter == null)
            {
                throw new ArgumentNullException("messageEnqueuedWatcherGetter");
            }

            _messageEnqueuedWatcherGetter = messageEnqueuedWatcherGetter;
        }

        public IArgumentBinding<IStorageQueue> TryCreate(ParameterInfo parameter)
        {
            if (!parameter.IsOut || parameter.ParameterType != typeof(byte[]).MakeByRefType())
            {
                return null;
            }

            return new ByteArrayArgumentBinding(_messageEnqueuedWatcherGetter);
        }

        private class ByteArrayArgumentBinding : IArgumentBinding<IStorageQueue>
        {
            private readonly IContextGetter<IMessageEnqueuedWatcher> _messageEnqueuedWatcherGetter;

            public ByteArrayArgumentBinding(IContextGetter<IMessageEnqueuedWatcher> messageEnqueuedWatcherGetter)
            {
                if (messageEnqueuedWatcherGetter == null)
                {
                    throw new ArgumentNullException("messageEnqueuedWatcherGetter");
                }

                _messageEnqueuedWatcherGetter = messageEnqueuedWatcherGetter;
            }

            public Type ValueType
            {
                get { return typeof(byte[]); }
            }

            /// <remarks>
            /// The out byte array parameter is processed as follows:
            /// <list type="bullet">
            /// <item>
            /// <description>
            /// If the value is <see langword="null"/>, no message will be sent.
            /// </description>
            /// </item>
            /// <item>
            /// <description>
            /// If the value is an empty byte array, a message with empty content will be sent.
            /// </description>
            /// </item>
            /// <item>
            /// <description>
            /// If the value is a non-empty byte array, a message with that content will be sent.
            /// </description>
            /// </item>
            /// </list>
            /// </remarks>
            public Task<IValueProvider> BindAsync(IStorageQueue value, ValueBindingContext context)
            {
                IValueProvider provider = new NonNullConverterValueBinder<byte[]>(value,
                    new ByteArrayToStorageQueueMessageConverter(value), _messageEnqueuedWatcherGetter.Value);
                return Task.FromResult(provider);
            }
        }
    }
}
