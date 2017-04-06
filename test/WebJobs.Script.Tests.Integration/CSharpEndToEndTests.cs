﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests
{
    public class CSharpEndToEndTests : EndToEndTestsBase<CSharpEndToEndTests.TestFixture>
    {
        public CSharpEndToEndTests(TestFixture fixture) : base(fixture)
        {
        }

        [Fact]
        public async Task ManualTrigger_Invoke_Succeeds()
        {
            await ManualTrigger_Invoke_SucceedsTest();
        }

        [Fact]
        public async Task QueueTriggerToBlob()
        {
            await QueueTriggerToBlobTest();
        }

        [Fact]
        public async Task ServiceBusQueueTriggerToBlobTest()
        {
            await ServiceBusQueueTriggerToBlobTestImpl();
        }

        [Fact]
        public async Task TwilioReferenceInvokeSucceeds()
        {
            await TwilioReferenceInvokeSucceedsImpl(isDotNet: true);
        }

        [Fact]
        public async Task MobileTables()
        {
            await MobileTablesTest(isDotNet: true);
        }

        [Fact]
        public async Task DocumentDB()
        {
            await DocumentDBTest();
        }

        [Fact]
        public async Task NotificationHub()
        {
            await NotificationHubTest("NotificationHubOut");
        }

        [Fact]
        public async Task NotificationHub_Out_Notification()
        {
            await NotificationHubTest("NotificationHubOutNotification");
        }

        [Fact]
        public async Task NotificationHubNative()
        {
            await NotificationHubTest("NotificationHubNative");
        }

        [Fact]
        public async Task MobileTablesTable()
        {
            var id = Guid.NewGuid().ToString();
            Dictionary<string, object> arguments = new Dictionary<string, object>()
            {
                { "input",  id }
            };

            await Fixture.Host.CallAsync("MobileTableTable", arguments);

            await WaitForMobileTableRecordAsync("Item", id);
        }

        [Fact]
        public async Task MultipleOutputs()
        {
            string id1 = Guid.NewGuid().ToString();
            string id2 = Guid.NewGuid().ToString();
            string id3 = Guid.NewGuid().ToString();

            JObject input = new JObject
            {
                { "Id1", id1 },
                { "Id2", id2 },
                { "Id3", id3 }
            };
            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "input", input.ToString() }
            };
            await Fixture.Host.CallAsync("MultipleOutputs", arguments);

            // verify all 3 output blobs were written
            var blob = Fixture.TestOutputContainer.GetBlockBlobReference(id1);
            await TestHelpers.WaitForBlobAsync(blob);
            string blobContent = blob.DownloadText();
            Assert.Equal("Test Blob 1", Utility.RemoveUtf8ByteOrderMark(blobContent));

            blob = Fixture.TestOutputContainer.GetBlockBlobReference(id2);
            await TestHelpers.WaitForBlobAsync(blob);
            blobContent = blob.DownloadText();
            Assert.Equal("Test Blob 2", Utility.RemoveUtf8ByteOrderMark(blobContent));

            blob = Fixture.TestOutputContainer.GetBlockBlobReference(id3);
            await TestHelpers.WaitForBlobAsync(blob);
            blobContent = blob.DownloadText();
            Assert.Equal("Test Blob 3", Utility.RemoveUtf8ByteOrderMark(blobContent));
        }

        [Fact]
        public async Task ScriptReference_LoadsScript()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://functions/myfunc");
            Dictionary<string, object> arguments = new Dictionary<string, object>()
            {
                { "req", request }
            };

            await Fixture.Host.CallAsync("LoadScriptReference", arguments);

            Assert.Equal("TestClass", request.Properties["LoadedScriptResponse"]);
        }

        [Fact]
        public async Task ApiHub()
        {
            await ApiHubTest();
        }

        [Fact]
        public async Task ApiHubTableClientBindingTest()
        {
            var textArgValue = ApiHubTestHelper.NewRandomString();

            // Ensure the test entity exists.
            await ApiHubTestHelper.EnsureEntityAsync(ApiHubTestHelper.EntityId1);

            // Test table client binding.
            await Fixture.Host.CallAsync("ApiHubTableClient",
                new Dictionary<string, object>()
                {
                    { ApiHubTestHelper.TextArg, textArgValue }
                });

            await ApiHubTestHelper.AssertTextUpdatedAsync(
                textArgValue, ApiHubTestHelper.EntityId1);
        }

        [Fact]
        public async Task ApiHubTableBindingTest()
        {
            var textArgValue = ApiHubTestHelper.NewRandomString();

            // Ensure the test entity exists.
            await ApiHubTestHelper.EnsureEntityAsync(ApiHubTestHelper.EntityId2);

            // Test table binding.
            TestInput input = new TestInput
            {
                Id = ApiHubTestHelper.EntityId2,
                Value = textArgValue
            };
            await Fixture.Host.CallAsync("ApiHubTable",
                new Dictionary<string, object>()
                {
                    { "input", JsonConvert.SerializeObject(input) }
                });

            await ApiHubTestHelper.AssertTextUpdatedAsync(
                textArgValue, ApiHubTestHelper.EntityId2);
        }

        [Fact]
        public async Task ApiHubTableEntityBindingTest()
        {
            var textArgValue = ApiHubTestHelper.NewRandomString();

            // Ensure the test entity exists.
            await ApiHubTestHelper.EnsureEntityAsync(ApiHubTestHelper.EntityId3);

            // Test table entity binding.
            TestInput input = new TestInput
            {
                Id = ApiHubTestHelper.EntityId3,
                Value = textArgValue
            };
            await Fixture.Host.CallAsync("ApiHubTableEntity",
                new Dictionary<string, object>()
                {
                    { "input", JsonConvert.SerializeObject(input) }
                });

            await ApiHubTestHelper.AssertTextUpdatedAsync(
                textArgValue, ApiHubTestHelper.EntityId3);
        }

        [Fact]
        public async Task SharedAssemblyDependenciesAreLoaded()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "http://functions/myfunc");
            Dictionary<string, object> arguments = new Dictionary<string, object>()
            {
                { "req", request }
            };

            await Fixture.Host.CallAsync("AssembliesFromSharedLocation", arguments);

            Assert.Equal("secondary type value", request.Properties["DependencyOutput"]);
        }

        [Fact]
        public async Task Scenario_RandGuidBinding_GeneratesRandomIDs()
        {
            var container = Fixture.BlobClient.GetContainerReference("scenarios-output");
            if (container.Exists())
            {
                foreach (CloudBlockBlob blob in container.ListBlobs())
                {
                    await blob.DeleteAsync();
                }
            }

            // Call 3 times - expect 3 separate output blobs
            for (int i = 0; i < 3; i++)
            {
                ScenarioInput input = new ScenarioInput
                {
                    Scenario = "randGuid",
                    Container = "scenarios-output",
                    Value = i.ToString()
                };
                Dictionary<string, object> arguments = new Dictionary<string, object>
                {
                    { "input", JsonConvert.SerializeObject(input) }
                };
                await Fixture.Host.CallAsync("Scenarios", arguments);
            }

            var blobs = container.ListBlobs().Cast<CloudBlockBlob>().ToArray();
            Assert.Equal(3, blobs.Length);
            foreach (var blob in blobs)
            {
                string content = blob.DownloadText();
                int blobInt = int.Parse(content.Trim(new char[] { '\uFEFF', '\u200B' }));
                Assert.True(blobInt >= 0 && blobInt <= 3);
            }
        }

        [Fact]
        public async Task HttpTrigger_Post_Dynamic()
        {
            var input = new JObject
            {
                { "name", "Mathew Charles" },
                { "location", "Seattle" }
            };

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/httptrigger-dynamic")),
                Method = HttpMethod.Post,
                Content = new StringContent(input.ToString())
            };
            request.SetConfiguration(Fixture.RequestConfiguration);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "input", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("HttpTrigger-Dynamic", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal("Name: Mathew Charles, Location: Seattle", body);
        }

        [Theory]
        [InlineData("application/json", "\"Name: Fabio Cavalcante, Location: Seattle\"")]
        [InlineData("application/xml", "<string xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/\">Name: Fabio Cavalcante, Location: Seattle</string>")]
        [InlineData("text/plain", "Name: Fabio Cavalcante, Location: Seattle")]
        public async Task HttpTrigger_GetWithAccept_NegotiatesContent(string accept, string expectedBody)
        {
            var input = new JObject
            {
                { "name", "Fabio Cavalcante" },
                { "location", "Seattle" }
            };

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/httptrigger-dynamic")),
                Method = HttpMethod.Post,
                Content = new StringContent(input.ToString())
            };
            request.SetConfiguration(Fixture.RequestConfiguration);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "input", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };

            await Fixture.Host.CallAsync("HttpTrigger-Dynamic", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(accept, response.Content.Headers.ContentType.MediaType);

            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal(expectedBody, body);
        }

        [Fact]
        public async Task Proxy_Http_Request()
        {
            var actualRequestObject = new HttpRequestMessage(HttpMethod.Post, "http://httpbin.org/post");

            actualRequestObject.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            actualRequestObject.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("test");
            actualRequestObject.Content.Headers.ContentLocation = new Uri("http://www.bing.com");
            actualRequestObject.Content.Headers.ContentRange = new ContentRangeHeaderValue(100);
            actualRequestObject.Content.Headers.Expires = new DateTimeOffset(DateTime.Now.AddDays(10));
            actualRequestObject.Content.Headers.LastModified = new DateTimeOffset(DateTime.Now.AddDays(-10));
            actualRequestObject.Headers.Add("User-Agent", "TestApp");
            actualRequestObject.Headers.Add("header1", "key1");
            actualRequestObject.Headers.Add("header2", new List<string> { "key2", "key21" });

            var originalRequest = ProxyHttpExtensions.Serialize(actualRequestObject);

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpRequest")),
                Method = HttpMethod.Post,
                Content = new StringContent(originalRequest)
            };
            request.SetConfiguration(Fixture.RequestConfiguration);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "req", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpRequest", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedRequestObject = new HttpRequestMessage();

            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedRequestObject));

            Assert.Equal(actualRequestObject.RequestUri.AbsoluteUri, expectedRequestObject.RequestUri.AbsoluteUri);
            Assert.Equal(actualRequestObject.Content.Headers.ContentType.MediaType, expectedRequestObject.Content.Headers.ContentType.MediaType);
            Assert.Equal(actualRequestObject.Content.Headers.ContentType.CharSet, expectedRequestObject.Content.Headers.ContentType.CharSet);
            Assert.Equal(actualRequestObject.Content.Headers.ContentDisposition.DispositionType, expectedRequestObject.Content.Headers.ContentDisposition.DispositionType);
            Assert.NotNull(expectedRequestObject.Headers.GetValues("ServerDateTime"));

            var actualContentString = await actualRequestObject.Content.ReadAsStringAsync();
            var expectedContentString = await expectedRequestObject.Content.ReadAsStringAsync();

            Assert.Equal(actualContentString, expectedContentString);
        }

        [Fact]
        public async Task Proxy_Http_Response()
        {
            var originalResponse = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, @"TestScripts\CSharp\ProxyHttpResponse\response.json"));

            var actualResponseObject = new HttpResponseMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(originalResponse, out actualResponseObject));

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpResponse")),
                Method = HttpMethod.Post,
                Content = new StringContent(originalResponse)
            };

            request.SetConfiguration(Fixture.RequestConfiguration);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "res", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpResponse", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedResponseObject = new HttpResponseMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedResponseObject));

            Assert.Equal(actualResponseObject.RequestMessage.RequestUri.AbsoluteUri, expectedResponseObject.RequestMessage.RequestUri.AbsoluteUri);
            Assert.Equal(actualResponseObject.Content.Headers.ContentType.MediaType, expectedResponseObject.Content.Headers.ContentType.MediaType);
            Assert.Equal(actualResponseObject.Content.Headers.ContentType.CharSet, expectedResponseObject.Content.Headers.ContentType.CharSet);
            Assert.NotNull(expectedResponseObject.Headers.GetValues("ResponseServerDateTime"));

            var actualContentString = await actualResponseObject.Content.ReadAsStringAsync();
            var expectedContentString = await expectedResponseObject.Content.ReadAsStringAsync();

            Assert.Equal(actualContentString, expectedContentString);
        }

        [Fact]
        public async Task Proxy_Http_Request_NoContent()
        {
            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpRequest")),
                Method = HttpMethod.Post,
            };
            request.SetConfiguration(Fixture.RequestConfiguration);

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "req", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpRequest", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedRequestObject = new HttpRequestMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedRequestObject));

            Assert.NotNull(expectedRequestObject.Headers.GetValues("ServerDateTime"));
            Assert.Null(expectedRequestObject.Content);
        }

        [Fact]
        public async Task Proxy_Http_Response_NoContent()
        {
            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpResponse")),
                Method = HttpMethod.Post,
            };

            request.SetConfiguration(Fixture.RequestConfiguration);

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "res", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpResponse", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedResponseObject = new HttpResponseMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedResponseObject));

            Assert.NotNull(expectedResponseObject.Headers.GetValues("ResponseServerDateTime"));
            Assert.Null(expectedResponseObject.Content);
            Assert.Null(expectedResponseObject.RequestMessage);
        }

        [Fact]
        public async Task Proxy_Http_Request_Utf32Content()
        {
            var actualRequestObject = new HttpRequestMessage(HttpMethod.Post, "http://httpbin.org/post");

            actualRequestObject.Content = new StringContent("{\"Key\":\"HELLO こんにちは добры дзень سلام 你好 नमस्ते  Здравствуйте\"}", Encoding.UTF32, "application/json");

            var originalRequest = ProxyHttpExtensions.Serialize(actualRequestObject);

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpRequest")),
                Method = HttpMethod.Post,
                Content = new StringContent(originalRequest)
            };
            request.SetConfiguration(Fixture.RequestConfiguration);

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "req", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpRequest", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedRequestObject = new HttpRequestMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedRequestObject));

            Assert.Equal(actualRequestObject.RequestUri.AbsoluteUri, expectedRequestObject.RequestUri.AbsoluteUri);
            Assert.Equal(actualRequestObject.Content.Headers.ContentType.MediaType, expectedRequestObject.Content.Headers.ContentType.MediaType);
            Assert.Equal(actualRequestObject.Content.Headers.ContentType.CharSet, expectedRequestObject.Content.Headers.ContentType.CharSet);

            var actualContentString = await actualRequestObject.Content.ReadAsStringAsync();
            var expectedContentString = await expectedRequestObject.Content.ReadAsStringAsync();

            Assert.Equal(actualContentString, expectedContentString);
        }

        [Fact]
        public async Task Proxy_Http_Request_BinaryContent()
        {
            var actualRequestObject = new HttpRequestMessage(HttpMethod.Post, "http://httpbin.org/post");

            var bytes = new byte[] { 0, 1, 2, 3, 4 };
            actualRequestObject.Content = new ByteArrayContent(bytes);
            actualRequestObject.Content.Headers.ContentType = new MediaTypeHeaderValue("image/*");

            var originalRequest = ProxyHttpExtensions.Serialize(actualRequestObject);

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpRequest")),
                Method = HttpMethod.Post,
                Content = new StringContent(originalRequest)
            };
            request.SetConfiguration(Fixture.RequestConfiguration);

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "req", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpRequest", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedRequestObject = new HttpRequestMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedRequestObject));

            Assert.Equal(actualRequestObject.RequestUri.AbsoluteUri, expectedRequestObject.RequestUri.AbsoluteUri);
            Assert.Equal(actualRequestObject.Content.Headers.ContentType.MediaType, expectedRequestObject.Content.Headers.ContentType.MediaType);

            var actualContentBytes = await actualRequestObject.Content.ReadAsByteArrayAsync();
            var expectedContentBytes = await expectedRequestObject.Content.ReadAsByteArrayAsync();

            Assert.True(actualContentBytes.SequenceEqual(expectedContentBytes));
        }

        [Fact]
        public async Task Proxy_Http_Request_NewObject()
        {
            var actualRequestObject = new HttpRequestMessage(HttpMethod.Post, "http://httpbin.org/post");

            actualRequestObject.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            actualRequestObject.Headers.Add("header1", "key1");

            var originalRequest = ProxyHttpExtensions.Serialize(actualRequestObject);

            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpRequestNewObject")),
                Method = HttpMethod.Post,
                Content = new StringContent(originalRequest)
            };
            request.SetConfiguration(Fixture.RequestConfiguration);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "req", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpRequestNewObject", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedRequestObject = new HttpRequestMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedRequestObject));

            Assert.NotNull(expectedRequestObject.Headers.GetValues("ServerDateTime"));
        }

        [Fact]
        public async Task Proxy_Http_Response_NewObject()
        {
            HttpRequestMessage request = new HttpRequestMessage
            {
                RequestUri = new Uri(string.Format("http://localhost/api/ProxyHttpResponseNewObject")),
                Method = HttpMethod.Post,
            };

            request.SetConfiguration(Fixture.RequestConfiguration);

            Dictionary<string, object> arguments = new Dictionary<string, object>
            {
                { "res", request },
                { ScriptConstants.SystemTriggerParameterName, request }
            };
            await Fixture.Host.CallAsync("ProxyHttpResponseNewObject", arguments);

            HttpResponseMessage response = (HttpResponseMessage)request.Properties[ScriptConstants.AzureFunctionsHttpResponseKey];
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var content = await response.Content.ReadAsStringAsync();

            var expectedResponseObject = new HttpResponseMessage();
            Assert.True(ProxyHttpExtensions.TryDeserialize(content, out expectedResponseObject));
            Assert.NotNull(expectedResponseObject.Headers.GetValues("ResponseServerDateTime"));
        }

        public class TestFixture : EndToEndTestFixture
        {
            private const string ScriptRoot = @"TestScripts\CSharp";

            static TestFixture()
            {
                CreateSharedAssemblies();
            }

            public TestFixture() : base(ScriptRoot, "csharp")
            {
            }

            private static void CreateSharedAssemblies()
            {
                string sharedAssembliesPath = Path.Combine(ScriptRoot, "SharedAssemblies");

                if (Directory.Exists(sharedAssembliesPath))
                {
                    Directory.Delete(sharedAssembliesPath, true);
                }

                Directory.CreateDirectory(sharedAssembliesPath);

                string secondaryDependencyPath = Path.Combine(sharedAssembliesPath, "SecondaryDependency.dll");

                string primaryReferenceSource = @"
using SecondaryDependency;

namespace PrimaryDependency
{
    public class Primary
    {
        public string GetValue()
        {
            var secondary = new Secondary();
            return secondary.GetSecondaryValue();
        }
    }
}";
                string secondaryReferenceSource = @"
namespace SecondaryDependency
{
    public class Secondary
    {
        public string GetSecondaryValue()
        {
            return ""secondary type value"";
        }
    }
}";
                var secondarySyntaxTree = CSharpSyntaxTree.ParseText(secondaryReferenceSource);
                Compilation secondaryCompilation = CSharpCompilation.Create("SecondaryDependency", new[] { secondarySyntaxTree })
                    .WithReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                    .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                secondaryCompilation.Emit(secondaryDependencyPath);

                var primarySyntaxTree = CSharpSyntaxTree.ParseText(primaryReferenceSource);
                Compilation primaryCompilation = CSharpCompilation.Create("PrimaryDependency", new[] { primarySyntaxTree })
                    .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                    .WithReferences(MetadataReference.CreateFromFile(secondaryDependencyPath), MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

                primaryCompilation.Emit(Path.Combine(sharedAssembliesPath, "PrimaryDependency.dll"));
            }
        }

        public class TestInput
        {
            public int Id { get; set; }
            public string Value { get; set; }
        }

        public class ScenarioInput
        {
            public string Scenario { get; set; }
            public string Container { get; set; }
            public string Value { get; set; }
        }
    }
}
