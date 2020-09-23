﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Extensions
{
    public class ExceptionExtensionsTests
    {
        [Fact]
        public void GetExceptionDetails_ReturnsExpectedResult()
        {
            Exception innerException = new InvalidOperationException("Some inner exception");
            Exception outerException = new Exception("some outer exception", innerException);
            Exception fullException;

            try
            {
                throw outerException;
            }
            catch (Exception e)
            {
                fullException = e;  // Outer exception will have stack trace whereas the inner exception's stack trace will be null
            }

            (string exceptionType, string exceptionMessage, string exceptionDetails) = fullException.GetExceptionDetails();

            Assert.Equal(exceptionType, "System.InvalidOperationException");
            Assert.Equal(exceptionMessage, "Some inner exception");
            Assert.Equal(exceptionDetails, "System.Exception : some outer exception ---> System.InvalidOperationException : Some inner exception \r\n   End of inner exception\r\n   at Microsoft.Azure.WebJobs.Script.Tests.Extensions.ExceptionExtensionsTests.GetExceptionDetails_ReturnsExpectedResult() at D:\\Repo_Functions\\runtime\\azure-functions-host\\test\\WebJobs.Script.Tests\\Extensions\\ExceptionExtensionsTests.cs : 20");
        }
    }
}
