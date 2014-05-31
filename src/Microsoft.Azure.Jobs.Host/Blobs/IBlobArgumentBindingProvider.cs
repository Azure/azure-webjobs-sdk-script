﻿using System;
using System.Reflection;
using Microsoft.Azure.Jobs.Host.Bindings;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Microsoft.Azure.Jobs.Host.Blobs
{
    internal interface IBlobArgumentBindingProvider
    {
        IArgumentBinding<ICloudBlob> TryCreate(ParameterInfo parameter);
    }
}
