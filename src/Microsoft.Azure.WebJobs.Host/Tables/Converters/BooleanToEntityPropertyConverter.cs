﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Azure.WebJobs.Host.Converters;
using Microsoft.WindowsAzure.Storage.Table;

namespace Microsoft.Azure.WebJobs.Host.Tables.Converters
{
    internal class BooleanToEntityPropertyConverter : IConverter<bool, EntityProperty>
    {
        public EntityProperty Convert(bool input)
        {
            return new EntityProperty(input);
        }
    }
}
