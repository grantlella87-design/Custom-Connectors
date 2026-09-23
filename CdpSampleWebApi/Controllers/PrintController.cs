using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Mvc;

namespace CdpSampleWebApi
{
    /// <summary>
    /// Forty-scale CustomPrint PDF and print layout endpoints (V13).
    /// </summary>
    [ApiController]
    [Route("gis/print")]
    public sealed class PrintController : ControllerBase
    {
        public const string DefaultLayoutItemId = "1adfd94dd6aa418096967f39d1f67470";
        public const string DefaultExportWebMapTaskUrl = "https://gis.nationalgrid.com/gp/rest/services/CustomPrint/GPServer/Export%20Web%20Map";
        public const string DefaultLayoutInfoTaskUrl = "https://gis.nationalgrid.com/gp/rest/services/CustomPrint/GPServer/Get%20Layout%20Templates%20Info";
        private static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(90);
        private readonly ArcGisRestClient _arcGis;

        public PrintController(ArcGisRestClient arcGis)
        {
            _arcGis = arcGis;
        }

        /// <summary>
        /// Submits an asynchronous CustomPrint Export Web Map job that renders a PDF with the Portal-hosted layout.
        /// Poll the returned job with gis/geoprocessing/gp/job and fetch <c>Output_File</c> with gp/result.
        /// </summary>
        [HttpPost("generateMapPdf")]
        public async Task<IActionResult> GenerateMapPdf([FromBody] GenerateMapPdfRequest request, CancellationToken cancellationToken)
        {
            string taskUrl;
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.WebMapJson))
                {
                    throw new ArgumentException("Web Map JSON is required.");
                }

                try
                {
                    using var webMap = JsonDocument.Parse(request.WebMapJson);
                    if (webMap.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new ArgumentException("Web Map JSON must be a JSON object.");
                    }
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException("Web Map JSON is not valid JSON: " + ex.Message);
                }

                taskUrl = ArcGisRestClient.ValidateGpTaskUrl(string.IsNullOrWhiteSpace(request.TaskUrl) ? DefaultExportWebMapTaskUrl : request.TaskUrl);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var parameters = new Dictionary<string, string>
            {
                ["Web_Map_as_JSON"] = request.WebMapJson,
                ["Format"] = "PDF",
                ["Layout_Item_ID"] = LayoutItemParameter(request.LayoutItemId),
            };

            using var json = await _arcGis.PostJsonAsync(taskUrl + "/submitJob", parameters, cancellationToken).ConfigureAwait(false);
            var result = JsonNode.Parse(json.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
            result["taskUrl"] = taskUrl;
            result["resultName"] = "Output_File";
            return Ok(result);
        }

        /// <summary>
        /// Returns the page and WEBMAP_MAP_FRAME dimensions of a Portal-hosted print layout.
        /// </summary>
        [HttpPost("layout-info")]
        public async Task<IActionResult> GetLayoutInfo([FromBody] LayoutInfoRequest request, CancellationToken cancellationToken)
        {
            string taskUrl;
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.LayoutItemId))
                {
                    throw new ArgumentException("Portal layout item ID is required.");
                }

                taskUrl = ArcGisRestClient.ValidateGpTaskUrl(string.IsNullOrWhiteSpace(request.TaskUrl) ? DefaultLayoutInfoTaskUrl : request.TaskUrl);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var parameters = new Dictionary<string, string> { ["Layout_Item_ID"] = LayoutItemParameter(request.LayoutItemId) };
            var value = await RunTaskAsync(taskUrl, parameters, cancellationToken).ConfigureAwait(false);
            return Ok(ParseLayoutInfo(request.LayoutItemId.Trim(), value));
        }

        /// <summary>
        /// Reads a Get Layout Templates Info output value (an array of templates, a single template, or either serialized as a string).
        /// </summary>
        public static LayoutInfoResponse ParseLayoutInfo(string layoutItemId, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(value.GetString() ?? "null");
                return ParseLayoutInfo(layoutItemId, inner.RootElement.Clone());
            }

            var template = value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().FirstOrDefault()
                : value;
            if (template.ValueKind != JsonValueKind.Object)
            {
                throw new ArcGisException("The layout info task returned no layout template.");
            }

            var (pageWidth, pageHeight) = ReadSize(template, "pageSize");
            var (frameWidth, frameHeight) = ReadSize(template, "webMapFrameSize");
            if (frameWidth == null)
            {
                (frameWidth, frameHeight) = ReadSize(template, "activeDataFrameSize");
            }

            var hasLegend = template.TryGetProperty("layoutOptions", out var options)
                && options.TryGetProperty("hasLegend", out var legend)
                && legend.ValueKind == JsonValueKind.True;

            return new LayoutInfoResponse
            {
                LayoutItemId = layoutItemId,
                LayoutTemplate = template.TryGetProperty("layoutTemplate", out var name) ? name.GetString() : null,
                MapFrameWidth = frameWidth,
                MapFrameHeight = frameHeight,
                Units = template.TryGetProperty("pageUnits", out var units) ? units.GetString() : "INCH",
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                HasLegend = hasLegend,
            };
        }

        private static (double? Width, double? Height) ReadSize(JsonElement template, string property)
        {
            if (template.TryGetProperty(property, out var size) && size.ValueKind == JsonValueKind.Array && size.GetArrayLength() >= 2)
            {
                return (size[0].GetDouble(), size[1].GetDouble());
            }

            return (null, null);
        }

        private static string LayoutItemParameter(string layoutItemId)
        {
            var id = string.IsNullOrWhiteSpace(layoutItemId) ? DefaultLayoutItemId : layoutItemId.Trim();
            return JsonSerializer.Serialize(new { id });
        }

        // Runs a GP task synchronously or asynchronously depending on how the task is published, and returns its first output value.
        private async Task<JsonElement> RunTaskAsync(string taskUrl, Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            string executionType;
            string outputName = null;
            using (var info = await _arcGis.GetJsonAsync(taskUrl, cancellationToken).ConfigureAwait(false))
            {
                executionType = info.RootElement.TryGetProperty("executionType", out var type) ? type.GetString() : null;
                if (info.RootElement.TryGetProperty("parameters", out var taskParameters))
                {
                    outputName = taskParameters.EnumerateArray()
                        .Where(p => p.TryGetProperty("direction", out var direction) && direction.GetString() == "esriGPParameterDirectionOutput")
                        .Select(p => p.GetProperty("name").GetString())
                        .FirstOrDefault();
                }
            }

            if (!string.Equals(executionType, "esriExecutionTypeAsynchronous", StringComparison.OrdinalIgnoreCase))
            {
                using var executed = await _arcGis.PostJsonAsync(taskUrl + "/execute", parameters, cancellationToken).ConfigureAwait(false);
                if (!executed.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                {
                    throw new ArcGisException("The GP task returned no results.");
                }

                return results[0].GetProperty("value").Clone();
            }

            string jobId;
            using (var submitted = await _arcGis.PostJsonAsync(taskUrl + "/submitJob", parameters, cancellationToken).ConfigureAwait(false))
            {
                jobId = submitted.RootElement.GetProperty("jobId").GetString();
            }

            var deadline = DateTime.UtcNow + JobTimeout;
            while (true)
            {
                using var status = await _arcGis.GetJsonAsync(taskUrl + "/jobs/" + Uri.EscapeDataString(jobId), cancellationToken).ConfigureAwait(false);
                var jobStatus = status.RootElement.TryGetProperty("jobStatus", out var s) ? s.GetString() : null;
                if (jobStatus == "esriJobSucceeded")
                {
                    break;
                }

                if (jobStatus is "esriJobFailed" or "esriJobCancelled" or "esriJobTimedOut" || DateTime.UtcNow > deadline)
                {
                    throw new ArcGisException($"GP job {jobId} did not succeed (status {jobStatus ?? "unknown"}).");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            using var result = await _arcGis.GetJsonAsync(taskUrl + "/jobs/" + Uri.EscapeDataString(jobId) + "/results/" + Uri.EscapeDataString(outputName ?? "Output_JSON"), cancellationToken).ConfigureAwait(false);
            return result.RootElement.GetProperty("value").Clone();
        }

        public sealed class GenerateMapPdfRequest
        {
            public string WebMapJson { get; set; }

            public string LayoutItemId { get; set; } = DefaultLayoutItemId;

            public string TaskUrl { get; set; } = DefaultExportWebMapTaskUrl;
        }

        public sealed class LayoutInfoRequest
        {
            public string LayoutItemId { get; set; } = DefaultLayoutItemId;

            public string TaskUrl { get; set; } = DefaultLayoutInfoTaskUrl;
        }

        public sealed class LayoutInfoResponse
        {
            public string LayoutItemId { get; set; }

            public string LayoutTemplate { get; set; }

            public double? MapFrameWidth { get; set; }

            public double? MapFrameHeight { get; set; }

            public string Units { get; set; }

            public double? PageWidth { get; set; }

            public double? PageHeight { get; set; }

            public bool HasLegend { get; set; }
        }
    }
}
