using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using PrivateBytesPerfCounterAnomalies.Domain.AnomalyDetection;
using PrivateBytesPerfCounterAnomalies.Domain.AppInsightsParsing;
using System;
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

        [FunctionName("CheckForPrivateBytesPerfCounterAnomalies")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            //converting data from specific Application Insigts response format to the one suitable for Anomaly Detection
            var result = JsonConvert.DeserializeObject<TablesResult>(GetTelemetry());
            var rows = result.Tables.First();

            var andetData = new AndetDataCollection();
            andetData.Series = rows.Rows.Select(i => new PerfCounterRow() { Timestamp = DateTime.Parse(i[0]), Value = (long)double.Parse(i[1]) }).OrderBy(i => i.Timestamp).ToList();

            var cnt = andetData.Series.Count();

            JsonSerializer serializer = new JsonSerializer
            {
                NullValueHandling = NullValueHandling.Ignore
            };

            andetData.Granularity = "minutely";

            var json = JsonConvert.SerializeObject(andetData, Formatting.None, new JsonSerializerSettings
            {
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                DateFormatString = "s"
            });

            log.LogInformation($"ApplicatrionInsights. JSON prepared for Anomaly Detection: \n\r {json}");

            //send perf counters to Anomaly Detection
            var anomalyDetectionResult = DetectAnomaliesBatch(json);
            //var anomalyDetectionResult = DetectAnomaliesLatest(json);
            if (anomalyDetectionResult.Contains("ErrorCode:"))
            {
                return new BadRequestObjectResult(anomalyDetectionResult);
            }

            return new OkObjectResult(anomalyDetectionResult);
            //

            //return (ActionResult)new OkObjectResult(json);
            // if not successfull:
            // new BadRequestObjectResult("Please pass a name on the query string or in the request body");
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

        public static string GetTelemetry()
        {
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Add("x-api-key", AzureAiApiKey);
            HttpResponseMessage response = client.GetAsync(GenerateAppInsightsQuery()).Result;
            if (response.IsSuccessStatusCode)
            {
                return response.Content.ReadAsStringAsync().Result;
            }
            else
            {
                return response.ReasonPhrase;
            }
        }

        private static string GenerateAppInsightsQuery()
        {
            // this query takes last 100 'Private Bytes' counter records records from Application Insights
            // this query summarizes (groups) values by minute (by average value) in order to make sure the data-series have minute granularity
            // otherwise Anomaly Detector throws an exception
            return $"https://api.applicationinsights.io/v1/apps/{AzureAiAppId}/query?query=performanceCounters | where tostring(customDimensions.Role) == 'CD' | where name == 'Private Bytes' | summarize avgValue=avg(value) by bin(timestamp, 1m) | project timestamp, tostring(avgValue) | order by timestamp desc| take 100 | order by timestamp asc";
        }
    }
}