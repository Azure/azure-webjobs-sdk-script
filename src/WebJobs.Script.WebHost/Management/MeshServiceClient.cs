// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.WebHost.Models;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;

namespace Microsoft.Azure.WebJobs.Script.WebHost.Management
{
    public class MeshServiceClient : IMeshServiceClient
    {
        private const string Operation = "operation";
        private readonly HttpClient _client;
        private readonly ILogger _logger;
        private readonly IEnvironment _environment;

        public MeshServiceClient(HttpClient client, IEnvironment environment, ILogger<MeshServiceClient> logger)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        public async Task<bool> MountCifs(string connectionString, string contentShare, string targetPath)
        {
            var sa = CloudStorageAccount.Parse(connectionString);
            var key = Convert.ToBase64String(sa.Credentials.ExportKey());
            HttpResponseMessage responseMessage = await SendAsync(new[]
            {
                new KeyValuePair<string, string>(Operation, "cifs"),
                new KeyValuePair<string, string>("host", sa.FileEndpoint.Host),
                new KeyValuePair<string, string>("accountName", sa.Credentials.AccountName),
                new KeyValuePair<string, string>("accountKey", key),
                new KeyValuePair<string, string>("contentShare", contentShare),
                new KeyValuePair<string, string>("targetPath", targetPath),
            });

            return responseMessage.IsSuccessStatusCode;
        }

        public Task MountBlob(string connectionString, string contentShare, string targetPath)
        {
            // todo: Implement once mesh init server supports mounting blobs
            throw new NotImplementedException(nameof(MountBlob));
        }

        public async Task MountFuse(string type, string filePath, string scriptPath)
            => await SendAsync(new[]
            {
                new KeyValuePair<string, string>(Operation, type),
                new KeyValuePair<string, string>("filePath", filePath),
                new KeyValuePair<string, string>("targetPath", scriptPath),
            });

        public async Task PublishContainerActivity(IEnumerable<ContainerFunctionExecutionActivity> activities)
        {
            _logger.LogDebug($"Publishing {activities.Count()} container activities");

            try
            {
                await Utility.InvokeWithRetriesAsync(async () =>
                {
                    await PublishActivities(activities);
                }, 2, TimeSpan.FromSeconds(0.5));
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"{nameof(PublishContainerActivity)}");
            }
        }

        public async Task NotifyHealthEvent(ContainerHealthEventType healthEventType, Type source, string details)
        {
            var healthEvent = new ContainerHealthEvent()
            {
                EventTime = DateTime.UtcNow,
                EventType = healthEventType,
                Details = details,
                Source = source.ToString()
            };

            var healthEventString = JsonConvert.SerializeObject(healthEvent);

            _logger.LogInformation($"Posting health event {healthEventString}");

            var responseMessage = await SendAsync(new[]
            {
                new KeyValuePair<string, string>(Operation, "add-health-event"),
                new KeyValuePair<string, string>("healthEvent", healthEventString),
            });

            _logger.LogInformation($"Posted health event status: {responseMessage.StatusCode}");
        }

        private async Task PublishActivities(IEnumerable<ContainerFunctionExecutionActivity> activities)
        {
            // Log one of the activities being published for debugging.
            _logger.LogDebug($"Publishing function execution activity {activities.FirstOrDefault()}");

            var operation = new[]
            {
                new KeyValuePair<string, string>(Operation, "add-fes"),
                new KeyValuePair<string, string>("content", JsonConvert.SerializeObject(activities)),
            };

            var response = await SendAsync(operation);
            response.EnsureSuccessStatusCode();
        }

        private async Task<HttpResponseMessage> SendAsync(IEnumerable<KeyValuePair<string, string>> formData)
        {
            var operationName = formData.FirstOrDefault(f => string.Equals(f.Key, Operation)).Value;
            _logger.LogDebug($"Sending mesh request {operationName}");
            var res = await _client.PostAsync(_environment.GetEnvironmentVariable(EnvironmentSettingNames.MeshInitURI),
                new FormUrlEncodedContent(formData));
            _logger.LogDebug($"Mesh response {res.StatusCode}");
            return res;
        }
    }
}