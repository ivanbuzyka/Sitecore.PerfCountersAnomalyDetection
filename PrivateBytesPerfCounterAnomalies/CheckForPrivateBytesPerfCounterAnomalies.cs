using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PrivateBytesPerfCounterAnomalies.Domain.AnomalyDetection;
using PrivateBytesPerfCounterAnomalies.Domain.AppInsightsParsing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace PrivateBytesPerfCounterAnomalies
{
    public static class CheckForPrivateBytesPerfCounterAnomalies
    {
        private static string AzureAiAppId = Environment.GetEnvironmentVariable("ApplicationInsightsAppId", EnvironmentVariableTarget.Process);
        private static string AzureAiApiKey = Environment.GetEnvironmentVariable("ApplicationInsightsApiKey", EnvironmentVariableTarget.Process);
        private static string AnomalyDetectionSubscriptionKey = Environment.GetEnvironmentVariable("AnomalyDetectionSubscriptionKey", EnvironmentVariableTarget.Process);
        private static string AnomalyDetectionEndpoint = Environment.GetEnvironmentVariable("AnomalyDetectionEndpoint", EnvironmentVariableTarget.Process);
        private static string AnomalyDetectionLatestPointDetectionUrl = Environment.GetEnvironmentVariable("AnomalyDetectionLatestPointDetectionUrl", EnvironmentVariableTarget.Process);
        private static string AnomalyDetectionBatchDetectionUrl = Environment.GetEnvironmentVariable("AnomalyDetectionBatchDetectionUrl", EnvironmentVariableTarget.Process);
        private static string AzureAiQueryMinutesAgo = Environment.GetEnvironmentVariable("AzureAiQueryMinutesAgo", EnvironmentVariableTarget.Process);

        [FunctionName("CheckForPrivateBytesPerfCounterAnomalies")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            // getting data from application insights
            var anomalyDetectionData = GetAppInsightsPrivateBytesTelemetry(log);

            // posting App Insights data to anomaly detector and getting result with anomaly rows
            var anomalyDataRows = GetAnomalyPerfCountersRows(anomalyDetectionData, log);

            var anomalyDataRowsJson = JsonConvert.SerializeObject(anomalyDataRows, Formatting.Indented, new JsonSerializerSettings
            {
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                DateFormatString = "s"
            });

            return new OkObjectResult(anomalyDataRowsJson);
            // if not successfull:
            // new BadRequestObjectResult("Please pass a name on the query string or in the request body");
        }

        #region private methods

        private static IEnumerable<PerfCounterRow> GetAnomalyPerfCountersRows(AndetDataCollection dataCollection, ILogger log)
        {
            var result = new List<PerfCounterRow>();

            var json = JsonConvert.SerializeObject(dataCollection, Formatting.None, new JsonSerializerSettings
            {
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                DateFormatString = "s"
            });

            log.LogInformation($"ApplicationInsights. JSON prepared for Anomaly Detection: \n\r {json}");

            //send perf counters to Anomaly Detection
            var anomalyDetectionResult = DetectAnomaliesBatch(json);

            //var anomalyDetectionResult = DetectAnomaliesLatest(json);
            if (anomalyDetectionResult.Contains("ErrorCode:"))
            {
                log.LogError(anomalyDetectionResult);
                return result;
            }

            var intermediateResult = JsonConvert.DeserializeObject<AndetResponseData>(anomalyDetectionResult);

            // selecting indexes of all anomaly rows
            int[] anomalyRowsIndexes = intermediateResult.IsAnomaly.Select((value, index) => new { value, index })
                .Where(t => t.value)
                .Select(t => t.index)
                .ToArray();

            result = anomalyRowsIndexes.Select(i => new PerfCounterRow() { Timestamp = dataCollection.Series[i].Timestamp, Value = dataCollection.Series[i].Value }).ToList();

            return result;
        }

        private static string DetectAnomaliesBatch(string requestData)
        {
            var result = RequestAnomalyDetection(
                AnomalyDetectionEndpoint,
                AnomalyDetectionBatchDetectionUrl,
                AnomalyDetectionSubscriptionKey,
                requestData).Result;

            return result;
        }

        private static string DetectAnomaliesLatest(string requestData)
        {
            var result = RequestAnomalyDetection(
                AnomalyDetectionEndpoint,
                AnomalyDetectionLatestPointDetectionUrl,
                AnomalyDetectionSubscriptionKey,
                requestData).Result;

            return result;
        }

        private static async Task<string> RequestAnomalyDetection(string baseAddress, string endpoint, string subscriptionKey, string requestData)
        {
            using (HttpClient client = new HttpClient { BaseAddress = new Uri(baseAddress) })
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

                var content = new StringContent(requestData, Encoding.UTF8, "application/json");
                var res = await client.PostAsync(endpoint, content);
                if (res.IsSuccessStatusCode)
                {
                    return await res.Content.ReadAsStringAsync();
                }
                else
                {
                    return $"ErrorCode: {res.StatusCode}, ErrorMessage: {await res.Content.ReadAsStringAsync()}";
                }
            }
        }

        public static AndetDataCollection GetAppInsightsPrivateBytesTelemetry(ILogger log)
        {
            var result = new AndetDataCollection
            {
                Granularity = "minutely"
            };

            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("x-api-key", AzureAiApiKey);
            HttpResponseMessage response = client.GetAsync(GenerateAppInsightsPrivateBytesQuery()).Result;

            if (!response.IsSuccessStatusCode)
            {
                log.LogError($"Error when getting telemetry data from App Insights: {response.Content.ReadAsStringAsync().Result}");
                return result;
            }

            var intermediateResult = JsonConvert.DeserializeObject<TablesResult>(response.Content.ReadAsStringAsync().Result);
            var rows = intermediateResult.Tables.First();
                        
            result.Series = rows.Rows.Select(i => new PerfCounterRow() { Timestamp = DateTime.Parse(i[0]), Value = (long)double.Parse(i[1]) }).OrderBy(i => i.Timestamp).ToList();

            return result;
        }

        private static string GenerateAppInsightsPrivateBytesQuery()
        {
            // this query takes last 100 'Private Bytes' counter records records from Application Insights
            // this query summarizes (groups) values by minute (by average value) in order to make sure the data-series have minute granularity
            // otherwise Anomaly Detector throws an exception            
            // The query below should return at least 12 records (requirement of the Anomaly Detector) therefore please pay attention that
            // AzureAiQueryMinutesAgo is correctly set and Application Insights has enough data
            var timeLimitation = string.IsNullOrEmpty(AzureAiQueryMinutesAgo) ? string.Empty : $"| where timestamp > now(-{AzureAiQueryMinutesAgo}m)";
            var query = $"https://api.applicationinsights.io/v1/apps/{AzureAiAppId}/query?query=performanceCounters " +
                        $"{timeLimitation}" +
                        $"| where tostring(customDimensions.Role) == 'CD' " +
                        $"| where name == 'Private Bytes' " +
                        $"| summarize avgValue=avg(value) by bin(timestamp, 1m) " +
                        $"| project timestamp, tostring(avgValue) " +
                        $"| order by timestamp desc" +
                        $"| take 100 " +
                        $"| order by timestamp asc";

            return query;
            //return $"https://api.applicationinsights.io/v1/apps/{AzureAiAppId}/query?query=performanceCounters | where tostring(customDimensions.Role) == 'CD' | where name == 'Private Bytes' | summarize avgValue=avg(value) by bin(timestamp, 1m) | project timestamp, tostring(avgValue) | order by timestamp desc| take 100 | order by timestamp asc";
        }

        #endregion
    }
}