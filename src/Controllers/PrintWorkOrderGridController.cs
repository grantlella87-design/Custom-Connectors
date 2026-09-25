// Copyright (c) National Grid.
#pragma warning disable CA1002, CA1034, CA1812, CA1848, CA2016, SA1000, SA1009, SA1101, SA1107, SA1116, SA1117, SA1122, SA1503, SA1505, SA1507, SA1508, SA1600, SA1601, SA1137

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace CdpSampleWebApi.Controllers
{
    /// <summary>
    /// Builds a grid index whose cell size is derived from the selected layout's
    /// map frame and a single scale value, keeps the cells that intersect a buffer
    /// around the proposed main, and prints one sheet per cell.
    ///
    /// The scale is the only zoom control. Cell size and printed extent are always
    /// the same, so the sheets tile the whole project no matter what scale is used.
    /// A tighter scale produces smaller cells and more sheets; it never clips the
    /// project.
    /// </summary>
    [ApiController]
    [Route("gis/print")]
    public sealed class PrintWorkOrderGridController : ControllerBase
    {
        private const string MapServiceUrl =
            "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer";

        private const string PortalRestUrl =
            "https://gis.nationalgrid.com/portal/sharing/rest";

        private const string CustomPrintItemId = "952293363e7247b99b8372c2db089073";
        private const string DefaultLayoutItemId = "1adfd94dd6aa418096967f39d1f67470";

        private const int ProposedMainLayerId = 54;
        private const int ProposedGroupLayerId = 49;

        private const string LandbaseServiceUrl =
            "https://gis.nationalgrid.com/arcgis/rest/services/MA/Landbase_MA/MapServer";

        private const int TownBoundaryLayerId = 243;
        private const int MapSpatialReference = 2249;

        // Forty scale: one inch on the page equals forty feet on the ground.
        private const double DefaultScale = 480;

        // Used only when the layout's map frame cannot be read and no override
        // is supplied. The response always reports which source was used.
        private const double FallbackFrameWidthInches = 30;
        private const double FallbackFrameHeightInches = 20;

        private const double DefaultBufferFeet = 50;

        private const int JobPollLimit = 300;
        private const int JobTimeoutSeconds = 900;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ArcGisOAuthTokenProvider _tokenProvider;

        public PrintWorkOrderGridController(
            IHttpClientFactory httpClientFactory,
            ArcGisOAuthTokenProvider tokenProvider)
        {
            _httpClientFactory = httpClientFactory;
            _tokenProvider = tokenProvider;
        }

        public sealed class GridPrintRequest
        {
            [JsonPropertyName("workOrderId")]
            public string WorkOrderId { get; set; } = string.Empty;

            [JsonPropertyName("dryRun")]
            public bool? DryRun { get; set; }

            /// <summary>
            /// The single zoom control. Cell size and printed extent both derive
            /// from this, so the grid always covers the whole project.
            /// </summary>
            [JsonPropertyName("scale")]
            public double? Scale { get; set; }

            /// <summary>Overrides the map frame width read from the layout item.</summary>
            [JsonPropertyName("mapFrameWidthInches")]
            public double? MapFrameWidthInches { get; set; }

            /// <summary>Overrides the map frame height read from the layout item.</summary>
            [JsonPropertyName("mapFrameHeightInches")]
            public double? MapFrameHeightInches { get; set; }

            [JsonPropertyName("bufferFeet")]
            public double? BufferFeet { get; set; }

            [JsonPropertyName("indexMapPrintCellsOnly")]
            public bool? IndexMapPrintCellsOnly { get; set; }

            [JsonPropertyName("layoutItemId")]
            public string? LayoutItemId { get; set; }

            [JsonPropertyName("layoutTemplate")]
            public string? LayoutTemplate { get; set; }

            /// <summary>MapServer sublayer IDs to draw. Defaults to the proposed group and main.</summary>
            [JsonPropertyName("visibleLayers")]
            public IList<int>? VisibleLayers { get; set; }

            /// <summary>Layer that receives the workorderid filter. Defaults to 54.</summary>
            [JsonPropertyName("definitionLayerId")]
            public int? DefinitionLayerId { get; set; }

            [JsonPropertyName("format")]
            public string? Format { get; set; }

            [JsonPropertyName("dpi")]
            public int? Dpi { get; set; }

            [JsonPropertyName("maxCells")]
            public int? MaxCells { get; set; }

            [JsonPropertyName("maxConcurrency")]
            public int? MaxConcurrency { get; set; }

            [JsonPropertyName("onlyCells")]
            public IList<string>? OnlyCells { get; set; }

            /// <summary>Shape of customTextElements: "array" (default) or "object".</summary>
            [JsonPropertyName("customTextFormat")]
            public string? CustomTextFormat { get; set; }

            /// <summary>Draw match line labels as map graphics inside the frame.</summary>
            [JsonPropertyName("matchlineGraphics")]
            public bool? MatchlineGraphics { get; set; }

            /// <summary>Inset from the frame edge as a fraction of the cell. Default 0.008.</summary>
            [JsonPropertyName("matchlineInset")]
            public double? MatchlineInset { get; set; }

            [JsonPropertyName("taskUrl")]
            public string? TaskUrl { get; set; }
        }

        private sealed class GridCell
        {
            public string Label { get; set; } = string.Empty;

            public int Row { get; set; }

            public int Col { get; set; }

            public double XMin { get; set; }

            public double YMin { get; set; }

            public double XMax { get; set; }

            public double YMax { get; set; }

            public bool WillPrint { get; set; }
        }

        private sealed class MapFrame
        {
            public double WidthInches { get; set; }

            public double HeightInches { get; set; }

            public string Source { get; set; } = string.Empty;

            public string? LayoutTitle { get; set; }
        }

        private sealed class GridModel
        {
            public List<GridCell> AllCells { get; } = new List<GridCell>();

            public List<GridCell> PrintCells { get; } = new List<GridCell>();

            public List<List<double[]>> Paths { get; } = new List<List<double[]>>();

            public int FeatureCount { get; set; }

            public int Rows { get; set; }

            public int Cols { get; set; }

            public double Scale { get; set; }

            public double CellWidthFeet { get; set; }

            public double CellHeightFeet { get; set; }

            public double OriginX { get; set; }

            public double OriginY { get; set; }

            public double BufferFeet { get; set; }

            public MapFrame Frame { get; set; } = new MapFrame();
        }

        // ------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------
        public sealed class TownCodeResult
        {
            public string? TownCode { get; set; }

            public string? Town { get; set; }

            public string? Division { get; set; }

            public string? MasterTown { get; set; }
        }

        /// <summary>
        /// Returns the town for a work order by intersecting the proposed main
        /// against the Town Boundary layer. Multiple towns are possible when a
        /// project crosses a boundary; the first is used for dynamic text.
        /// </summary>
        [HttpGet("workorder/towncode")]
        public async Task<IActionResult> GetTownCodeAsync([FromQuery] string workOrderId)
        {
            if (string.IsNullOrWhiteSpace(workOrderId))
                return BadRequest(new { error = "workOrderId is required." });

            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();
            var result = await LookupTownsAsync(client, token, workOrderId).ConfigureAwait(false);

            if (result.Error is not null) return StatusCode(502, result.Error);

            return Ok(new
            {
                workOrderId,
                townCode = result.Towns.FirstOrDefault()?.TownCode,
                town = result.Towns.FirstOrDefault()?.Town,
                division = result.Towns.FirstOrDefault()?.Division,
                townCount = result.Towns.Count,
                towns = result.Towns,
            });
        }

        private async Task<(List<TownCodeResult> Towns, object? Error)> LookupTownsAsync(
            HttpClient client,
            string token,
            string workOrderId)
        {
            var towns = new List<TownCodeResult>();

            // 1. Envelope of the proposed main, in the town layer's own SR so no
            //    reprojection is needed for the spatial filter.
            var where = BuildWhere(workOrderId);
            var extentUrl = $"{MapServiceUrl}/{ProposedMainLayerId}/query";

            using var extentForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["token"] = token,
                ["where"] = where,
                ["returnExtentOnly"] = "true",
                ["returnGeometry"] = "false",
            });

            using var extentResponse = await client
                .PostAsync(extentUrl, extentForm, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            var extentJson = await extentResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!TryParseJson(extentJson, out var extentDoc) || extentDoc is null)
                return (towns, new { stage = "mainExtent", preview = Preview(extentJson) });

            string? geometryJson = null;
            using (extentDoc)
            {
                if (extentDoc.RootElement.TryGetProperty("error", out var extentError))
                    return (towns, new { stage = "mainExtent", error = extentError.Clone() });

                if (extentDoc.RootElement.TryGetProperty("extent", out var extent))
                {
                    geometryJson = extent.GetRawText();
                }
            }

            if (string.IsNullOrWhiteSpace(geometryJson))
                return (towns, new { stage = "mainExtent", error = "No extent returned for the work order." });

            // 2. Town boundaries intersecting that envelope.
            var townUrl = $"{LandbaseServiceUrl}/{TownBoundaryLayerId}/query";

            using var townForm = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["token"] = token,
                ["geometry"] = geometryJson,
                ["geometryType"] = "esriGeometryEnvelope",
                ["spatialRel"] = "esriSpatialRelIntersects",
                ["outFields"] = "towncode,town,division,mastertown,name_gas",
                ["returnGeometry"] = "false",
                ["where"] = "1=1",
            });

            using var townResponse = await client
                .PostAsync(townUrl, townForm, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            var townJson = await townResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!TryParseJson(townJson, out var townDoc) || townDoc is null)
                return (towns, new { stage = "townBoundary", preview = Preview(townJson) });

            using (townDoc)
            {
                if (townDoc.RootElement.TryGetProperty("error", out var townError))
                    return (towns, new { stage = "townBoundary", error = townError.Clone() });

                if (townDoc.RootElement.TryGetProperty("features", out var features))
                {
                    foreach (var feature in features.EnumerateArray())
                    {
                        if (!feature.TryGetProperty("attributes", out var attributes)) continue;

                        towns.Add(new TownCodeResult
                        {
                            TownCode = ReadString(attributes, "towncode"),
                            Town = ReadString(attributes, "town"),
                            Division = ReadString(attributes, "division"),
                            MasterTown = ReadString(attributes, "mastertown"),
                        });
                    }
                }
            }

            return (towns, null);
        }

        private static string? ReadString(JsonElement attributes, string name)
        {
            if (!attributes.TryGetProperty(name, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        public sealed class SubmitResult
        {
            public string Cell { get; set; } = string.Empty;

            public string? JobId { get; set; }

            public string? Status { get; set; }

            public string? Error { get; set; }
        }

        /// <summary>
        /// Submits one print job per cell and returns immediately with the job
        /// IDs. Poll each with GET job/{jobId}. Designed for Power Automate,
        /// where a connector action must return well under 120 seconds.
        /// </summary>
        [HttpPost("workorder/grid/submit")]
        public async Task<IActionResult> SubmitGridAsync([FromBody] GridPrintRequest request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.WorkOrderId))
                return BadRequest(new { error = "workOrderId is required." });

            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();

            var build = await BuildGridAsync(client, token, request).ConfigureAwait(false);
            if (build.Error is not null) return StatusCode(502, build.Error);

            var grid = build.Grid!;
            var taskResolve = await ResolveTaskUrlAsync(client, token, request.TaskUrl).ConfigureAwait(false);
            if (taskResolve.Error is not null) return StatusCode(502, taskResolve.Error);

            var selected = grid.PrintCells.ToList();
            if (request.OnlyCells is { Count: > 0 })
            {
                var wanted = new HashSet<string>(request.OnlyCells, StringComparer.OrdinalIgnoreCase);
                selected = selected.Where(c => wanted.Contains(c.Label)).ToList();
            }

            var townLookup = await LookupTownsAsync(client, token, request.WorkOrderId).ConfigureAwait(false);
            var townCode = townLookup.Towns.FirstOrDefault()?.TownCode;

            // Submission is cheap, so all cells are submitted concurrently and the
            // print server queues them.
            var submitTasks = selected.Select(async cell =>
            {
                var webMap = BuildCellWebMap(cell, grid, request, token, townCode);
                return await SubmitJobAsync(client, token, taskResolve.TaskUrl!, webMap, request, cell.Label)
                    .ConfigureAwait(false);
            });

            var submissions = await Task.WhenAll(submitTasks).ConfigureAwait(false);

            return Ok(new
            {
                workOrderId = request.WorkOrderId,
                townCode,
                taskUrl = taskResolve.TaskUrl,
                submitted = submissions.Count(s => s.JobId is not null),
                failed = submissions.Count(s => s.JobId is null),
                pollUrlTemplate = "/gis/print/job/{jobId}?taskUrl={taskUrl}",
                jobs = submissions,
            });
        }

        /// <summary>Returns the status of a single print job, and the output URL when finished.</summary>
        [HttpGet("job/{jobId}")]
        public async Task<IActionResult> GetJobStatusAsync(string jobId, [FromQuery] string? taskUrl)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return BadRequest(new { error = "jobId is required." });

            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();

            var resolved = taskUrl;
            if (string.IsNullOrWhiteSpace(resolved))
            {
                var taskResolve = await ResolveTaskUrlAsync(client, token, null).ConfigureAwait(false);
                if (taskResolve.Error is not null) return StatusCode(502, taskResolve.Error);
                resolved = taskResolve.TaskUrl;
            }

            var jobUrl = $"{resolved!.TrimEnd('/')}/jobs/{jobId}";
            using var statusResponse = await client
                .GetAsync($"{jobUrl}?f=json&token={Uri.EscapeDataString(token)}", HttpContext.RequestAborted)
                .ConfigureAwait(false);
            var statusJson = await statusResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!TryParseJson(statusJson, out var statusDoc) || statusDoc is null)
                return StatusCode(502, new { stage = "jobStatus", preview = Preview(statusJson) });

            var jobStatus = "unknown";
            var messages = new List<string>();

            using (statusDoc)
            {
                if (statusDoc.RootElement.TryGetProperty("jobStatus", out var statusElement))
                    jobStatus = statusElement.GetString() ?? jobStatus;

                if (statusDoc.RootElement.TryGetProperty("messages", out var messageArray))
                {
                    foreach (var message in messageArray.EnumerateArray())
                    {
                        if (message.TryGetProperty("description", out var description))
                            messages.Add(description.GetString() ?? string.Empty);
                    }
                }
            }

            var isComplete = jobStatus is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled" or "esriJobTimedOut";
            string? outputUrl = null;

            if (jobStatus == "esriJobSucceeded")
            {
                using var resultResponse = await client
                    .GetAsync($"{jobUrl}/results/Output_File?f=json&token={Uri.EscapeDataString(token)}", HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                var resultJson = await resultResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (TryParseJson(resultJson, out var resultDoc) && resultDoc is not null)
                {
                    using (resultDoc)
                    {
                        if (resultDoc.RootElement.TryGetProperty("value", out var value) &&
                            value.TryGetProperty("url", out var urlElement))
                        {
                            outputUrl = urlElement.GetString();
                        }
                    }
                }
            }

            return Ok(new
            {
                jobId,
                jobStatus,
                isComplete,
                succeeded = jobStatus == "esriJobSucceeded",
                outputUrl,
                messages,
                taskUrl = resolved,
            });
        }

        private async Task<SubmitResult> SubmitJobAsync(
            HttpClient client,
            string token,
            string taskUrl,
            object webMap,
            GridPrintRequest request,
            string label)
        {
            var fields = BuildPrintFields(webMap, request, token);

            try
            {
                using var form = new FormUrlEncodedContent(fields);
                using var response = await client
                    .PostAsync(taskUrl.TrimEnd('/') + "/submitJob", form, HttpContext.RequestAborted)
                    .ConfigureAwait(false);

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!TryParseJson(json, out var doc) || doc is null)
                    return new SubmitResult { Cell = label, Error = Preview(json) };

                using (doc)
                {
                    if (doc.RootElement.TryGetProperty("error", out var error))
                        return new SubmitResult { Cell = label, Error = error.GetRawText() };

                    if (!doc.RootElement.TryGetProperty("jobId", out var jobIdElement))
                        return new SubmitResult { Cell = label, Error = "No jobId returned." };

                    return new SubmitResult
                    {
                        Cell = label,
                        JobId = jobIdElement.GetString(),
                        Status = doc.RootElement.TryGetProperty("jobStatus", out var s) ? s.GetString() : "esriJobSubmitted",
                    };
                }
            }
            catch (HttpRequestException ex)
            {
                return new SubmitResult { Cell = label, Error = ex.Message };
            }
        }

        private Dictionary<string, string> BuildPrintFields(
            object webMap,
            GridPrintRequest request,
            string token)
        {
            var layoutItemId = string.IsNullOrWhiteSpace(request.LayoutItemId)
                ? DefaultLayoutItemId
                : request.LayoutItemId!;
            var layoutTemplate = string.IsNullOrWhiteSpace(request.LayoutTemplate)
                ? "MAP_ONLY"
                : request.LayoutTemplate!;
            var format = string.IsNullOrWhiteSpace(request.Format)
                ? "Portable Document Format (PDF)"
                : request.Format!;

            var fields = new Dictionary<string, string>
            {
                ["f"] = "json",
                ["token"] = token,
                ["Web_Map_as_JSON"] = JsonSerializer.Serialize(webMap),
                ["Format"] = format,
                ["Layout_Template"] = layoutTemplate,
            };

            if (!string.Equals(layoutItemId, "none", StringComparison.OrdinalIgnoreCase))
                fields["Layout_Item_ID"] = JsonSerializer.Serialize(new { id = layoutItemId });

            return fields;
        }
        /// <summary>
        /// Returns the Web Map JSON that would be sent for one cell, so the
        /// customTextElements payload can be inspected without printing.
        /// </summary>
        [HttpGet("debug/webmap")]
        public async Task<IActionResult> DebugWebMapAsync(
            [FromQuery] string workOrderId,
            [FromQuery] string? cell,
            [FromQuery] double? scale,
            [FromQuery] double? bufferFeet)
        {
            if (string.IsNullOrWhiteSpace(workOrderId))
                return BadRequest(new { error = "workOrderId is required." });

            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();

            var request = new GridPrintRequest
            {
                WorkOrderId = workOrderId,
                Scale = scale,
                BufferFeet = bufferFeet,
            };

            var build = await BuildGridAsync(client, token, request).ConfigureAwait(false);
            if (build.Error is not null) return StatusCode(502, build.Error);

            var grid = build.Grid!;
            var target = string.IsNullOrWhiteSpace(cell)
                ? grid.PrintCells.FirstOrDefault()
                : grid.PrintCells.FirstOrDefault(c => string.Equals(c.Label, cell, StringComparison.OrdinalIgnoreCase));

            if (target is null)
                return NotFound(new { error = "Cell not found in the print set.", available = grid.PrintCells.Select(c => c.Label) });

            var townLookup = await LookupTownsAsync(client, token, workOrderId).ConfigureAwait(false);
            var townCode = townLookup.Towns.FirstOrDefault()?.TownCode;

            var webMap = BuildCellWebMap(target, grid, request, token, townCode);
            var serialized = JsonSerializer.Serialize(webMap, new JsonSerializerOptions { WriteIndented = true });

            // Redact the token before returning the payload.
            serialized = System.Text.RegularExpressions.Regex.Replace(
                serialized,
                "(\"token\"\\s*:\\s*\")[^\"]+",
                "$1REDACTED");

            return Ok(new
            {
                cell = target.Label,
                townCode,
                townCodeIsEmpty = string.IsNullOrEmpty(townCode),
                layoutItemId = DefaultLayoutItemId,
                webMapJson = serialized,
            });
        }

        [HttpGet("layers")]
        public async Task<IActionResult> ListLayersAsync()
        {
            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();
            var url = $"{MapServiceUrl}?f=json&token={Uri.EscapeDataString(token)}";

            using var response = await client.GetAsync(url, HttpContext.RequestAborted).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!TryParseJson(json, out var doc) || doc is null)
                return StatusCode(502, new { stage = "layers", preview = Preview(json) });

            using (doc)
            {
                var layers = new List<object>();
                if (doc.RootElement.TryGetProperty("layers", out var layerArray))
                {
                    foreach (var layer in layerArray.EnumerateArray())
                    {
                        layers.Add(new
                        {
                            id = layer.TryGetProperty("id", out var id) ? id.GetInt32() : -1,
                            name = layer.TryGetProperty("name", out var name) ? name.GetString() : null,
                            parentLayerId = layer.TryGetProperty("parentLayerId", out var parent) ? parent.GetInt32() : -1,
                            defaultVisibility = layer.TryGetProperty("defaultVisibility", out var vis) && vis.GetBoolean(),
                            minScale = layer.TryGetProperty("minScale", out var min) ? min.GetDouble() : 0,
                            maxScale = layer.TryGetProperty("maxScale", out var max) ? max.GetDouble() : 0,
                        });
                    }
                }

                return Ok(new { serviceUrl = MapServiceUrl, count = layers.Count, layers });
            }
        }

        [HttpGet("layouts")]
        public async Task<IActionResult> ListLayoutsAsync()
        {
            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();
            var url = $"{PortalRestUrl}/search?f=json&q=type%3A%22Layout%22&num=100&token={Uri.EscapeDataString(token)}";

            using var response = await client.GetAsync(url, HttpContext.RequestAborted).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!TryParseJson(json, out var doc) || doc is null)
                return StatusCode(502, new { stage = "layouts", preview = Preview(json) });

            using (doc)
            {
                var layouts = new List<object>();
                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                        layouts.Add(new
                        {
                            id,
                            title = item.TryGetProperty("title", out var title) ? title.GetString() : null,
                            owner = item.TryGetProperty("owner", out var owner) ? owner.GetString() : null,
                            isDefault = string.Equals(id, DefaultLayoutItemId, StringComparison.OrdinalIgnoreCase),
                        });
                    }
                }

                return Ok(new { count = layouts.Count, defaultLayoutItemId = DefaultLayoutItemId, layouts });
            }
        }

        /// <summary>
        /// Reports the map frame size that would be used for a layout, and where
        /// that number came from. Use this to confirm cell sizing before printing.
        /// </summary>
        [HttpGet("layout/frame")]
        public async Task<IActionResult> GetLayoutFrameAsync(
            [FromQuery] string? layoutItemId,
            [FromQuery] double? scale)
        {
            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();
            var itemId = string.IsNullOrWhiteSpace(layoutItemId) ? DefaultLayoutItemId : layoutItemId!;
            var frame = await ResolveMapFrameAsync(client, token, itemId, null, null).ConfigureAwait(false);

            var effectiveScale = scale ?? DefaultScale;

            return Ok(new
            {
                layoutItemId = itemId,
                layoutTitle = frame.LayoutTitle,
                mapFrameWidthInches = frame.WidthInches,
                mapFrameHeightInches = frame.HeightInches,
                frameSource = frame.Source,
                scale = effectiveScale,
                cellWidthFeet = frame.WidthInches / 12.0 * effectiveScale,
                cellHeightFeet = frame.HeightInches / 12.0 * effectiveScale,
            });
        }

        // ------------------------------------------------------------------
        // Index map
        // ------------------------------------------------------------------
        [HttpPost("workorder/grid/index")]
        public async Task<IActionResult> PrintGridIndexMapAsync([FromBody] GridPrintRequest request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.WorkOrderId))
                return BadRequest(new { error = "workOrderId is required." });

            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();

            var build = await BuildGridAsync(client, token, request).ConfigureAwait(false);
            if (build.Error is not null) return StatusCode(502, build.Error);

            var grid = build.Grid!;
            var taskResolve = await ResolveTaskUrlAsync(client, token, request.TaskUrl).ConfigureAwait(false);
            if (taskResolve.Error is not null) return StatusCode(502, taskResolve.Error);

            var renderer = await GetLayerRendererAsync(client, token, request).ConfigureAwait(false);
            var webMap = BuildIndexWebMap(grid, token, request, renderer);

            var result = await SubmitAndPollAsync(
                client,
                token,
                taskResolve.TaskUrl!,
                webMap,
                request,
                "IndexMap").ConfigureAwait(false);

            return Ok(new
            {
                workOrderId = request.WorkOrderId,
                indexMap = result,
                scale = grid.Scale,
                mapFrameWidthInches = grid.Frame.WidthInches,
                mapFrameHeightInches = grid.Frame.HeightInches,
                frameSource = grid.Frame.Source,
                bufferFeet = grid.BufferFeet,
                cellWidthFeet = grid.CellWidthFeet,
                cellHeightFeet = grid.CellHeightFeet,
                totalCells = grid.AllCells.Count,
                printCells = grid.PrintCells.Count,
                printCellLabels = grid.PrintCells.Select(c => c.Label),
                skippedCellLabels = grid.AllCells.Where(c => !c.WillPrint).Select(c => c.Label),
            });
        }

        // ------------------------------------------------------------------
        // Grid print
        // ------------------------------------------------------------------
        [HttpPost("workorder/grid")]
        public async Task<IActionResult> PrintWorkOrderGridAsync([FromBody] GridPrintRequest request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.WorkOrderId))
                return BadRequest(new { error = "workOrderId is required." });

            var token = await _tokenProvider
                .GetAccessTokenAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);

            var client = CreateClient();

            var build = await BuildGridAsync(client, token, request).ConfigureAwait(false);
            if (build.Error is not null) return StatusCode(502, build.Error);

            var grid = build.Grid!;

            var selected = grid.PrintCells.ToList();
            if (request.OnlyCells is { Count: > 0 })
            {
                var wanted = new HashSet<string>(request.OnlyCells, StringComparer.OrdinalIgnoreCase);
                selected = selected.Where(c => wanted.Contains(c.Label)).ToList();
            }

            var gridSummary = BuildGridSummary(grid, selected, request);

            var dryRun = request.DryRun ?? true;
            if (dryRun)
            {
                var concurrencyForEstimate = Math.Max(request.MaxConcurrency ?? 3, 1);
                return Ok(new
                {
                    dryRun = true,
                    message = "Grid computed. Call again with dryRun=false to print these cells.",
                    estimatedMinutes = Math.Round(selected.Count * 4.6 / concurrencyForEstimate, 1),
                    grid = gridSummary,
                });
            }

            var taskResolve = await ResolveTaskUrlAsync(client, token, request.TaskUrl).ConfigureAwait(false);
            if (taskResolve.Error is not null) return StatusCode(502, taskResolve.Error);

            var maxCells = request.MaxCells ?? 12;
            var toPrint = selected.Take(maxCells).ToList();
            var concurrency = Math.Clamp(request.MaxConcurrency ?? 3, 1, 6);

            var blockingTownLookup = await LookupTownsAsync(client, token, request.WorkOrderId).ConfigureAwait(false);
            var blockingTownCode = blockingTownLookup.Towns.FirstOrDefault()?.TownCode;

            using var gate = new SemaphoreSlim(concurrency);
            var tasks = toPrint.Select(async cell =>
            {
                await gate.WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                try
                {
                    var webMap = BuildCellWebMap(cell, grid, request, token, blockingTownCode);
                    return await SubmitAndPollAsync(
                        client,
                        token,
                        taskResolve.TaskUrl!,
                        webMap,
                        request,
                        cell.Label).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            return Ok(new
            {
                dryRun = false,
                grid = gridSummary,
                printedCells = results.Length,
                skippedCells = Math.Max(selected.Count - toPrint.Count, 0),
                taskUrl = taskResolve.TaskUrl,
                results,
            });
        }

        private static object BuildGridSummary(
            GridModel grid,
            List<GridCell> selected,
            GridPrintRequest request)
        {
            return new
            {
                workOrderId = request.WorkOrderId,
                featureCount = grid.FeatureCount,
                scale = grid.Scale,
                mapFrameWidthInches = grid.Frame.WidthInches,
                mapFrameHeightInches = grid.Frame.HeightInches,
                frameSource = grid.Frame.Source,
                layoutTitle = grid.Frame.LayoutTitle,
                bufferFeet = grid.BufferFeet,
                cellWidthFeet = grid.CellWidthFeet,
                cellHeightFeet = grid.CellHeightFeet,
                rows = grid.Rows,
                cols = grid.Cols,
                totalCells = grid.AllCells.Count,
                intersectingCells = grid.PrintCells.Count,
                selectedCells = selected.Count,
                visibleLayers = (request.VisibleLayers is { Count: > 0 })
                    ? request.VisibleLayers.ToArray()
                    : new[] { ProposedGroupLayerId, ProposedMainLayerId },
                skippedCellLabels = grid.AllCells.Where(c => !c.WillPrint).Select(c => c.Label),
                cells = selected.Select(c => new
                {
                    c.Label,
                    c.Row,
                    c.Col,
                    xmin = c.XMin,
                    ymin = c.YMin,
                    xmax = c.XMax,
                    ymax = c.YMax,
                }),
            };
        }

        // ------------------------------------------------------------------
        // Map frame resolution
        // ------------------------------------------------------------------
        private async Task<MapFrame> ResolveMapFrameAsync(
            HttpClient client,
            string token,
            string layoutItemId,
            double? widthOverride,
            double? heightOverride)
        {
            var frame = new MapFrame
            {
                WidthInches = FallbackFrameWidthInches,
                HeightInches = FallbackFrameHeightInches,
                Source = "fallbackDefault",
            };

            // Explicit overrides always win, so a known-good frame can be forced.
            if (widthOverride.HasValue && heightOverride.HasValue &&
                widthOverride.Value > 0 && heightOverride.Value > 0)
            {
                frame.WidthInches = widthOverride.Value;
                frame.HeightInches = heightOverride.Value;
                frame.Source = "requestOverride";
                return frame;
            }

            try
            {
                var infoUrl = $"{PortalRestUrl}/content/items/{layoutItemId}?f=json&token={Uri.EscapeDataString(token)}";
                using (var infoResponse = await client.GetAsync(infoUrl, HttpContext.RequestAborted).ConfigureAwait(false))
                {
                    var infoJson = await infoResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (TryParseJson(infoJson, out var infoDoc) && infoDoc is not null)
                    {
                        using (infoDoc)
                        {
                            if (infoDoc.RootElement.TryGetProperty("title", out var title))
                                frame.LayoutTitle = title.GetString();
                        }
                    }
                }

                var dataUrl = $"{PortalRestUrl}/content/items/{layoutItemId}/data?f=json&token={Uri.EscapeDataString(token)}";
                using var dataResponse = await client.GetAsync(dataUrl, HttpContext.RequestAborted).ConfigureAwait(false);
                var dataJson = await dataResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!TryParseJson(dataJson, out var dataDoc) || dataDoc is null)
                    return frame;

                using (dataDoc)
                {
                    if (TryFindMapFrame(dataDoc.RootElement, out var width, out var height))
                    {
                        frame.WidthInches = width;
                        frame.HeightInches = height;
                        frame.Source = "layoutMapFrame";
                        return frame;
                    }

                    if (TryFindPageSize(dataDoc.RootElement, out var pageWidth, out var pageHeight))
                    {
                        // The page is larger than the map frame, so this is reported
                        // distinctly rather than being presented as the frame size.
                        frame.WidthInches = pageWidth;
                        frame.HeightInches = pageHeight;
                        frame.Source = "layoutPageSize";
                        return frame;
                    }
                }
            }
            catch (HttpRequestException)
            {
                return frame;
            }

            return frame;
        }

        private static bool TryFindMapFrame(JsonElement element, out double width, out double height)
        {
            width = 0;
            height = 0;

            if (element.ValueKind == JsonValueKind.Object)
            {
                var isMapFrame = element.TryGetProperty("type", out var type) &&
                                 string.Equals(type.GetString(), "CIMMapFrame", StringComparison.OrdinalIgnoreCase);

                if (isMapFrame && element.TryGetProperty("frame", out var frameElement))
                {
                    // ArcGIS Pro stores the map frame as a rings polygon in page
                    // inches, so the extent is derived from the ring vertices.
                    if (frameElement.TryGetProperty("rings", out var rings) &&
                        rings.ValueKind == JsonValueKind.Array)
                    {
                        double xmin = double.MaxValue, ymin = double.MaxValue;
                        double xmax = double.MinValue, ymax = double.MinValue;
                        var found = false;

                        foreach (var ring in rings.EnumerateArray())
                        {
                            if (ring.ValueKind != JsonValueKind.Array) continue;

                            foreach (var point in ring.EnumerateArray())
                            {
                                if (point.ValueKind != JsonValueKind.Array) continue;
                                if (point.GetArrayLength() < 2) continue;

                                var x = point[0].GetDouble();
                                var y = point[1].GetDouble();

                                if (x < xmin) xmin = x;
                                if (y < ymin) ymin = y;
                                if (x > xmax) xmax = x;
                                if (y > ymax) ymax = y;
                                found = true;
                            }
                        }

                        if (found)
                        {
                            width = xmax - xmin;
                            height = ymax - ymin;
                            if (width > 0 && height > 0) return true;
                        }
                    }

                    if (frameElement.TryGetProperty("width", out var w) &&
                        frameElement.TryGetProperty("height", out var h))
                    {
                        width = w.GetDouble();
                        height = h.GetDouble();
                        if (width > 0 && height > 0) return true;
                    }

                    if (frameElement.TryGetProperty("xmin", out var fxmin) &&
                        frameElement.TryGetProperty("xmax", out var fxmax) &&
                        frameElement.TryGetProperty("ymin", out var fymin) &&
                        frameElement.TryGetProperty("ymax", out var fymax))
                    {
                        width = Math.Abs(fxmax.GetDouble() - fxmin.GetDouble());
                        height = Math.Abs(fymax.GetDouble() - fymin.GetDouble());
                        if (width > 0 && height > 0) return true;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindMapFrame(property.Value, out width, out height)) return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindMapFrame(item, out width, out height)) return true;
                }
            }

            width = 0;
            height = 0;
            return false;
        }

        private static bool TryFindPageSize(JsonElement element, out double width, out double height)
        {
            width = 0;
            height = 0;

            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("page", out var page) &&
                    page.TryGetProperty("width", out var w) &&
                    page.TryGetProperty("height", out var h))
                {
                    width = w.GetDouble();
                    height = h.GetDouble();
                    if (width > 0 && height > 0) return true;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindPageSize(property.Value, out width, out height)) return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindPageSize(item, out width, out height)) return true;
                }
            }

            width = 0;
            height = 0;
            return false;
        }

        // ------------------------------------------------------------------
        // Grid construction
        // ------------------------------------------------------------------
        private async Task<(GridModel? Grid, object? Error)> BuildGridAsync(
            HttpClient client,
            string token,
            GridPrintRequest request)
        {
            var scale = request.Scale ?? DefaultScale;
            var bufferFeet = request.BufferFeet ?? DefaultBufferFeet;

            if (scale <= 0)
                return (null, new { error = "scale must be greater than zero." });

            if (bufferFeet < 0)
                return (null, new { error = "bufferFeet cannot be negative." });

            var layoutItemId = string.IsNullOrWhiteSpace(request.LayoutItemId)
                ? DefaultLayoutItemId
                : request.LayoutItemId!;

            var frame = await ResolveMapFrameAsync(
                client,
                token,
                layoutItemId,
                request.MapFrameWidthInches,
                request.MapFrameHeightInches).ConfigureAwait(false);

            // Cell size is the map frame at the requested scale. The printed extent
            // uses the same numbers, so sheets tile the project exactly.
            var cellWidthFeet = frame.WidthInches / 12.0 * scale;
            var cellHeightFeet = frame.HeightInches / 12.0 * scale;

            var definitionLayerId = request.DefinitionLayerId ?? ProposedMainLayerId;
            var where = BuildWhere(request.WorkOrderId);
            var queryUrl = $"{MapServiceUrl}/{definitionLayerId}/query";
            var queryJson = string.Empty;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                using var queryForm = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json",
                    ["token"] = token,
                    ["where"] = where,
                    ["outFields"] = "workorderid",
                    ["returnGeometry"] = "true",
                    ["outSR"] = MapSpatialReference.ToString(CultureInfo.InvariantCulture),
                });

                using var queryResponse = await client
                    .PostAsync(queryUrl, queryForm, HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                queryJson = await queryResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (queryJson.TrimStart().StartsWith("{", StringComparison.Ordinal)) break;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(5), HttpContext.RequestAborted).ConfigureAwait(false);
            }

            if (!TryParseJson(queryJson, out var queryDoc) || queryDoc is null)
                return (null, new { stage = "query", preview = Preview(queryJson) });

            var grid = new GridModel
            {
                Scale = scale,
                CellWidthFeet = cellWidthFeet,
                CellHeightFeet = cellHeightFeet,
                BufferFeet = bufferFeet,
                Frame = frame,
            };

            var segments = new List<(double X1, double Y1, double X2, double Y2)>();
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            using (queryDoc)
            {
                if (queryDoc.RootElement.TryGetProperty("error", out var queryError))
                    return (null, new { stage = "query", error = queryError.Clone() });

                if (!queryDoc.RootElement.TryGetProperty("features", out var features) ||
                    features.GetArrayLength() == 0)
                {
                    return (null, new { stage = "query", error = "No proposed main features found." });
                }

                grid.FeatureCount = features.GetArrayLength();

                foreach (var feature in features.EnumerateArray())
                {
                    if (!feature.TryGetProperty("geometry", out var geometry)) continue;
                    if (!geometry.TryGetProperty("paths", out var paths)) continue;

                    foreach (var path in paths.EnumerateArray())
                    {
                        var capturedPath = new List<double[]>();
                        double? previousX = null;
                        double? previousY = null;

                        foreach (var point in path.EnumerateArray())
                        {
                            if (point.GetArrayLength() < 2) continue;

                            var x = point[0].GetDouble();
                            var y = point[1].GetDouble();

                            capturedPath.Add(new[] { x, y });

                            if (x < minX) minX = x;
                            if (y < minY) minY = y;
                            if (x > maxX) maxX = x;
                            if (y > maxY) maxY = y;

                            if (previousX.HasValue)
                                segments.Add((previousX.Value, previousY!.Value, x, y));

                            previousX = x;
                            previousY = y;
                        }

                        if (capturedPath.Count > 0)
                            grid.Paths.Add(capturedPath);
                    }
                }
            }

            if (segments.Count == 0)
                return (null, new { stage = "extent", error = "No usable polyline geometry." });

            // The grid must cover the features plus the buffer, so the buffered
            // extent drives the row and column count.
            var spanMinX = minX - bufferFeet;
            var spanMaxX = maxX + bufferFeet;
            var spanMinY = minY - bufferFeet;
            var spanMaxY = maxY + bufferFeet;

            var spanWidth = spanMaxX - spanMinX;
            var spanHeight = spanMaxY - spanMinY;

            var cols = Math.Max((int)Math.Ceiling(spanWidth / cellWidthFeet), 1);
            var rows = Math.Max((int)Math.Ceiling(spanHeight / cellHeightFeet), 1);

            var gridWidth = cols * cellWidthFeet;
            var gridHeight = rows * cellHeightFeet;
            var originX = spanMinX - ((gridWidth - spanWidth) / 2.0);
            var originY = spanMaxY + ((gridHeight - spanHeight) / 2.0);

            grid.Rows = rows;
            grid.Cols = cols;
            grid.OriginX = originX;
            grid.OriginY = originY;

            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var cell = new GridCell
                    {
                        Row = row + 1,
                        Col = col + 1,
                        Label = $"{(char)('A' + row)}{col + 1}",
                        XMin = originX + (col * cellWidthFeet),
                        XMax = originX + ((col + 1) * cellWidthFeet),
                        YMax = originY - (row * cellHeightFeet),
                        YMin = originY - ((row + 1) * cellHeightFeet),
                    };

                    cell.WillPrint = SegmentsIntersectCell(segments, cell, bufferFeet);
                    grid.AllCells.Add(cell);

                    if (cell.WillPrint)
                        grid.PrintCells.Add(cell);
                }
            }

            return (grid, null);
        }

        // ------------------------------------------------------------------
        // Web Map construction
        // ------------------------------------------------------------------
        private object BuildCellWebMap(
            GridCell cell,
            GridModel grid,
            GridPrintRequest request,
            string token,
            string? townCode = null)
        {
            var dpi = request.Dpi ?? 300;
            var where = BuildWhere(request.WorkOrderId);
            var definitionLayerId = request.DefinitionLayerId ?? ProposedMainLayerId;

            var values = new Dictionary<string, string>
            {
                ["towncode"] = townCode ?? string.Empty,
                ["pagename"] = cell.Label,
                ["pagenumber"] = PageNumberLabel(grid, cell),
                ["matchnorth"] = NeighbourLabel(grid, cell, -1, 0),
                ["matchsouth"] = NeighbourLabel(grid, cell, 1, 0),
                ["matcheast"] = NeighbourLabel(grid, cell, 0, 1),
                ["matchwest"] = NeighbourLabel(grid, cell, 0, -1),
            };

            // Still sent, so the layout fills itself if the service ever gains
            // customTextElements support.
            object customTextElements;
            if (string.Equals(request.CustomTextFormat, "object", StringComparison.OrdinalIgnoreCase))
            {
                customTextElements = values;
            }
            else
            {
                customTextElements = values
                    .Select(pair => new Dictionary<string, string> { [pair.Key] = pair.Value })
                    .ToList();
            }

            var layers = new List<object>
            {
                BuildMaterialViewLayer(token, where, request.VisibleLayers, definitionLayerId),
            };

            // Opt in. Match lines are drawn inside the map frame because the
            // service will not fill the layout's own text elements.
            if (request.MatchlineGraphics == true)
            {
                var matchlines = BuildMatchlineGraphicLayer(grid, cell, request.MatchlineInset);
                if (matchlines is not null) layers.Add(matchlines);
            }

            return new
            {
                mapOptions = new
                {
                    extent = new
                    {
                        xmin = cell.XMin,
                        ymin = cell.YMin,
                        xmax = cell.XMax,
                        ymax = cell.YMax,
                        spatialReference = new { wkid = MapSpatialReference },
                    },
                    scale = grid.Scale,
                    spatialReference = new { wkid = MapSpatialReference },
                },
                operationalLayers = layers,
                exportOptions = new
                {
                    dpi,
                    outputSize = new[]
                    {
                        (int)Math.Round(grid.Frame.WidthInches * dpi),
                        (int)Math.Round(grid.Frame.HeightInches * dpi),
                    },
                },
                layoutOptions = new
                {
                    titleText = $"Grid {cell.Label}",
                    customTextElements,
                },
            };
        }
        /// <summary>Position of this cell within the printing set, for example "4 of 14".</summary>
        private static string PageNumberLabel(GridModel grid, GridCell cell)
        {
            var ordered = grid.PrintCells
                .OrderBy(c => c.Row)
                .ThenBy(c => c.Col)
                .ToList();

            var index = ordered.FindIndex(c => c.Row == cell.Row && c.Col == cell.Col);
            if (index < 0) return string.Empty;

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} of {1}",
                index + 1,
                ordered.Count);
        }
        /// <summary>
        /// Match line labels drawn as map graphics at the edges of the cell.
        /// Used when the print service will not fill the layout's own dynamic
        /// text. Only labels for neighbours that are actually printing appear.
        /// </summary>
        private static object? BuildMatchlineGraphicLayer(
            GridModel grid,
            GridCell cell,
            double? insetFraction)
        {
            // Small default so the text sits against the neatline. The halo needs
            // a little clearance or the glyph edges are clipped by the frame.
            var fraction = insetFraction ?? 0.008;
            if (fraction < 0) fraction = 0;

            var insetX = (cell.XMax - cell.XMin) * fraction;
            var insetY = (cell.YMax - cell.YMin) * fraction;
            var centerX = (cell.XMin + cell.XMax) / 2.0;
            var centerY = (cell.YMin + cell.YMax) / 2.0;

            var labels = new List<(string Text, double X, double Y, int Angle, string VAlign)>();

            // Vertical alignment anchors the text against the edge rather than
            // centring it on the point, so it hugs the frame.
            var north = NeighbourLabel(grid, cell, -1, 0);
            if (!string.IsNullOrEmpty(north))
                labels.Add(("MATCH LINE - SEE SHEET " + north, centerX, cell.YMax - insetY, 0, "top"));

            var south = NeighbourLabel(grid, cell, 1, 0);
            if (!string.IsNullOrEmpty(south))
                labels.Add(("MATCH LINE - SEE SHEET " + south, centerX, cell.YMin + insetY, 0, "bottom"));

            var east = NeighbourLabel(grid, cell, 0, 1);
            if (!string.IsNullOrEmpty(east))
                labels.Add(("MATCH LINE - SEE SHEET " + east, cell.XMax - insetX, centerY, 90, "top"));

            var west = NeighbourLabel(grid, cell, 0, -1);
            if (!string.IsNullOrEmpty(west))
                labels.Add(("MATCH LINE - SEE SHEET " + west, cell.XMin + insetX, centerY, 90, "bottom"));

            if (labels.Count == 0) return null;

            var features = new List<object>();
            var objectId = 1;

            foreach (var label in labels)
            {
                features.Add(new
                {
                    attributes = new Dictionary<string, object>
                    {
                        ["OBJECTID"] = objectId++,
                        ["label"] = label.Text,
                    },
                    geometry = new
                    {
                        x = label.X,
                        y = label.Y,
                        spatialReference = new { wkid = MapSpatialReference },
                    },
                    symbol = new
                    {
                        type = "esriTS",
                        color = new[] { 0, 0, 0, 255 },
                        haloColor = new[] { 255, 255, 255, 235 },
                        haloSize = 1.5,
                        horizontalAlignment = "center",
                        verticalAlignment = label.VAlign,
                        angle = label.Angle,
                        text = label.Text,
                        font = new
                        {
                            family = "Arial",
                            size = 11,
                            weight = "bold",
                        },
                    },
                });
            }

            return new
            {
                id = "matchlineLabels",
                title = "Match lines",
                opacity = 1,
                visibility = true,
                featureCollection = new
                {
                    layers = new object[]
                    {
                        new
                        {
                            layerDefinition = new
                            {
                                name = "Match lines",
                                geometryType = "esriGeometryPoint",
                                objectIdField = "OBJECTID",
                                fields = BuildLabelFields(),
                                drawingInfo = new
                                {
                                    renderer = new
                                    {
                                        type = "simple",
                                        symbol = new
                                        {
                                            type = "esriSMS",
                                            style = "esriSMSCircle",
                                            color = new[] { 0, 0, 0, 0 },
                                            size = 1,
                                            outline = new
                                            {
                                                type = "esriSLS",
                                                style = "esriSLSNull",
                                                color = new[] { 0, 0, 0, 0 },
                                                width = 0,
                                            },
                                        },
                                    },
                                },
                            },
                            featureSet = new
                            {
                                geometryType = "esriGeometryPoint",
                                spatialReference = new { wkid = MapSpatialReference },
                                features,
                            },
                        },
                    },
                },
            };
        }

        private static string NeighbourLabel(GridModel grid, GridCell cell, int rowOffset, int colOffset)
        {
            var row = cell.Row + rowOffset;
            var col = cell.Col + colOffset;

            if (row < 1 || row > grid.Rows || col < 1 || col > grid.Cols)
                return string.Empty;

            var neighbour = grid.PrintCells.FirstOrDefault(c => c.Row == row && c.Col == col);
            return neighbour?.Label ?? string.Empty;
        }

        private object BuildIndexWebMap(
            GridModel grid,
            string token,
            GridPrintRequest request,
            JsonElement? serviceRenderer)
        {
            var dpi = request.Dpi ?? 300;
            var where = BuildWhere(request.WorkOrderId);
            var printCellsOnly = request.IndexMapPrintCellsOnly ?? false;
            var definitionLayerId = request.DefinitionLayerId ?? ProposedMainLayerId;

            var padX = grid.CellWidthFeet * 0.1;
            var padY = grid.CellHeightFeet * 0.1;

            var extent = new
            {
                xmin = grid.OriginX - padX,
                ymin = grid.OriginY - (grid.Rows * grid.CellHeightFeet) - padY,
                xmax = grid.OriginX + (grid.Cols * grid.CellWidthFeet) + padX,
                ymax = grid.OriginY + padY,
                spatialReference = new { wkid = MapSpatialReference },
            };

            var layers = new List<object>
            {
                BuildMaterialViewLayer(token, where, request.VisibleLayers, definitionLayerId),
            };

            if (!printCellsOnly)
            {
                var skipped = grid.AllCells.Where(c => !c.WillPrint).ToList();
                if (skipped.Count > 0)
                {
                    layers.Add(BuildCellPolygonLayer(
                        "gridCellsSkipped",
                        "Cells not printed",
                        skipped,
                        new[] { 150, 150, 150, 25 },
                        new[] { 120, 120, 120, 180 },
                        "esriSLSDash",
                        1));
                }
            }

            layers.Add(BuildCellPolygonLayer(
                "gridCellsPrinted",
                "Cells to print",
                grid.PrintCells,
                new[] { 0, 168, 84, 40 },
                new[] { 0, 110, 55, 255 },
                "esriSLSSolid",
                2));

            layers.Add(BuildMainLineLayer(grid, serviceRenderer));

            var labelCells = printCellsOnly ? grid.PrintCells : grid.AllCells;
            layers.Add(BuildLabelLayer(labelCells));

            return new
            {
                mapOptions = new
                {
                    extent,
                    spatialReference = new { wkid = MapSpatialReference },
                },
                operationalLayers = layers,
                exportOptions = new
                {
                    dpi,
                    outputSize = new[]
                    {
                        (int)Math.Round(grid.Frame.WidthInches * dpi),
                        (int)Math.Round(grid.Frame.HeightInches * dpi),
                    },
                },
                layoutOptions = new
                {
                    titleText = $"Index Map - Work Order {request.WorkOrderId}",
                },
            };
        }

        private static object BuildMaterialViewLayer(
            string token,
            string where,
            IList<int>? visibleLayers,
            int definitionLayerId)
        {
            var layerIds = (visibleLayers is { Count: > 0 })
                ? visibleLayers.ToArray()
                : new[] { ProposedGroupLayerId, ProposedMainLayerId };

            return new
            {
                id = "MaterialViewMA",
                title = "Material View MA",
                url = MapServiceUrl,
                layerType = "ArcGISMapServiceLayer",
                visibility = true,
                opacity = 1,
                token,
                visibleLayers = layerIds,
                layers = new object[]
                {
                    new
                    {
                        id = definitionLayerId,
                        visibility = true,
                        layerDefinition = new { definitionExpression = where },
                    },
                },
            };
        }

        private static object BuildCellPolygonLayer(
            string id,
            string title,
            IEnumerable<GridCell> cells,
            int[] fillColor,
            int[] outlineColor,
            string outlineStyle,
            double outlineWidth)
        {
            var symbol = new
            {
                type = "esriSFS",
                style = "esriSFSSolid",
                color = fillColor,
                outline = new
                {
                    type = "esriSLS",
                    style = outlineStyle,
                    color = outlineColor,
                    width = outlineWidth,
                },
            };

            var features = new List<object>();
            var objectId = 1;

            foreach (var cell in cells)
            {
                features.Add(new
                {
                    attributes = new Dictionary<string, object>
                    {
                        ["OBJECTID"] = objectId++,
                        ["label"] = cell.Label,
                    },
                    geometry = new
                    {
                        rings = new[]
                        {
                            new[]
                            {
                                new[] { cell.XMin, cell.YMin },
                                new[] { cell.XMin, cell.YMax },
                                new[] { cell.XMax, cell.YMax },
                                new[] { cell.XMax, cell.YMin },
                                new[] { cell.XMin, cell.YMin },
                            },
                        },
                        spatialReference = new { wkid = MapSpatialReference },
                    },
                    symbol,
                });
            }

            return new
            {
                id,
                title,
                opacity = 1,
                visibility = true,
                featureCollection = new
                {
                    layers = new object[]
                    {
                        new
                        {
                            layerDefinition = new
                            {
                                name = title,
                                geometryType = "esriGeometryPolygon",
                                objectIdField = "OBJECTID",
                                fields = BuildLabelFields(),
                                drawingInfo = new
                                {
                                    renderer = new
                                    {
                                        type = "simple",
                                        symbol,
                                    },
                                },
                            },
                            featureSet = new
                            {
                                geometryType = "esriGeometryPolygon",
                                spatialReference = new { wkid = MapSpatialReference },
                                features,
                            },
                        },
                    },
                },
            };
        }

        private static object BuildMainLineLayer(GridModel grid, JsonElement? serviceRenderer)
        {
            object fallbackSymbol = new
            {
                type = "esriSLS",
                style = "esriSLSSolid",
                color = new[] { 214, 0, 28, 255 },
                width = 3,
            };

            object rendererObject;
            var usingServiceRenderer = serviceRenderer.HasValue;

            if (usingServiceRenderer)
            {
                rendererObject = JsonSerializer.Deserialize<JsonElement>(serviceRenderer!.Value.GetRawText());
            }
            else
            {
                rendererObject = new
                {
                    type = "simple",
                    symbol = fallbackSymbol,
                };
            }

            var features = new List<object>();
            var objectId = 1;

            foreach (var path in grid.Paths)
            {
                var feature = new Dictionary<string, object>
                {
                    ["attributes"] = new Dictionary<string, object>
                    {
                        ["OBJECTID"] = objectId++,
                        ["label"] = "Proposed main",
                    },
                    ["geometry"] = new
                    {
                        paths = new[] { path.ToArray() },
                        spatialReference = new { wkid = MapSpatialReference },
                    },
                };

                if (!usingServiceRenderer)
                {
                    feature["symbol"] = fallbackSymbol;
                }

                features.Add(feature);
            }

            return new
            {
                id = "proposedMainGraphic",
                title = "Proposed main",
                opacity = 1,
                visibility = true,
                featureCollection = new
                {
                    layers = new object[]
                    {
                        new
                        {
                            layerDefinition = new
                            {
                                name = "Proposed main",
                                geometryType = "esriGeometryPolyline",
                                objectIdField = "OBJECTID",
                                fields = BuildLabelFields(),
                                drawingInfo = new
                                {
                                    renderer = rendererObject,
                                },
                            },
                            featureSet = new
                            {
                                geometryType = "esriGeometryPolyline",
                                spatialReference = new { wkid = MapSpatialReference },
                                features,
                            },
                        },
                    },
                },
            };
        }

        private static object BuildLabelLayer(IEnumerable<GridCell> cells)
        {
            var features = new List<object>();
            var objectId = 1;

            foreach (var cell in cells)
            {
                var textSymbol = new
                {
                    type = "esriTS",
                    color = cell.WillPrint
                        ? new[] { 0, 90, 45, 255 }
                        : new[] { 120, 120, 120, 255 },
                    haloColor = new[] { 255, 255, 255, 230 },
                    haloSize = 1,
                    horizontalAlignment = "center",
                    verticalAlignment = "middle",
                    text = cell.Label,
                    font = new
                    {
                        family = "Arial",
                        size = cell.WillPrint ? 14 : 10,
                        weight = cell.WillPrint ? "bold" : "normal",
                    },
                };

                features.Add(new
                {
                    attributes = new Dictionary<string, object>
                    {
                        ["OBJECTID"] = objectId++,
                        ["label"] = cell.Label,
                    },
                    geometry = new
                    {
                        x = (cell.XMin + cell.XMax) / 2.0,
                        y = (cell.YMin + cell.YMax) / 2.0,
                        spatialReference = new { wkid = MapSpatialReference },
                    },
                    symbol = textSymbol,
                });
            }

            return new
            {
                id = "gridCellLabels",
                title = "Page labels",
                opacity = 1,
                visibility = true,
                featureCollection = new
                {
                    layers = new object[]
                    {
                        new
                        {
                            layerDefinition = new
                            {
                                name = "Page labels",
                                geometryType = "esriGeometryPoint",
                                objectIdField = "OBJECTID",
                                fields = BuildLabelFields(),
                                drawingInfo = new
                                {
                                    renderer = new
                                    {
                                        type = "simple",
                                        symbol = new
                                        {
                                            type = "esriSMS",
                                            style = "esriSMSCircle",
                                            color = new[] { 0, 0, 0, 0 },
                                            size = 1,
                                            outline = new
                                            {
                                                type = "esriSLS",
                                                style = "esriSLSNull",
                                                color = new[] { 0, 0, 0, 0 },
                                                width = 0,
                                            },
                                        },
                                    },
                                },
                            },
                            featureSet = new
                            {
                                geometryType = "esriGeometryPoint",
                                spatialReference = new { wkid = MapSpatialReference },
                                features,
                            },
                        },
                    },
                },
            };
        }

        private static object[] BuildLabelFields()
        {
            return new object[]
            {
                new
                {
                    name = "OBJECTID",
                    alias = "OBJECTID",
                    type = "esriFieldTypeOID",
                },
                new
                {
                    name = "label",
                    alias = "label",
                    type = "esriFieldTypeString",
                    length = 32,
                },
            };
        }

        // ------------------------------------------------------------------
        // Service renderer
        // ------------------------------------------------------------------
        private JsonElement? _cachedRenderer;
        private bool _rendererFetched;

        private async Task<JsonElement?> GetLayerRendererAsync(
            HttpClient client,
            string token,
            GridPrintRequest request)
        {
            if (_rendererFetched) return _cachedRenderer;
            _rendererFetched = true;

            var layerId = request.DefinitionLayerId ?? ProposedMainLayerId;

            try
            {
                var url = $"{MapServiceUrl}/{layerId}?f=json&token={Uri.EscapeDataString(token)}";
                using var response = await client.GetAsync(url, HttpContext.RequestAborted).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!TryParseJson(json, out var doc) || doc is null) return null;

                using (doc)
                {
                    if (doc.RootElement.TryGetProperty("drawingInfo", out var drawingInfo) &&
                        drawingInfo.TryGetProperty("renderer", out var renderer))
                    {
                        _cachedRenderer = renderer.Clone();
                        return _cachedRenderer;
                    }
                }
            }
            catch (HttpRequestException)
            {
                return null;
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Print job plumbing
        // ------------------------------------------------------------------
        private async Task<object> SubmitAndPollAsync(
            HttpClient client,
            string token,
            string taskUrl,
            object webMap,
            GridPrintRequest request,
            string label)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(JobTimeoutSeconds));
            var jobToken = cts.Token;

            var layoutItemId = string.IsNullOrWhiteSpace(request.LayoutItemId)
                ? DefaultLayoutItemId
                : request.LayoutItemId!;
            var layoutTemplate = string.IsNullOrWhiteSpace(request.LayoutTemplate)
                ? "MAP_ONLY"
                : request.LayoutTemplate!;
            var format = string.IsNullOrWhiteSpace(request.Format)
                ? "Portable Document Format (PDF)"
                : request.Format!;

            var fields = new Dictionary<string, string>
            {
                ["f"] = "json",
                ["token"] = token,
                ["Web_Map_as_JSON"] = JsonSerializer.Serialize(webMap),
                ["Format"] = format,
                ["Layout_Template"] = layoutTemplate,
            };

            if (!string.Equals(layoutItemId, "none", StringComparison.OrdinalIgnoreCase))
                fields["Layout_Item_ID"] = JsonSerializer.Serialize(new { id = layoutItemId });

            var watch = System.Diagnostics.Stopwatch.StartNew();
            string? jobId = null;

            try
            {
                using (var form = new FormUrlEncodedContent(fields))
                using (var submitResponse = await client
                    .PostAsync(taskUrl.TrimEnd('/') + "/submitJob", form, jobToken)
                    .ConfigureAwait(false))
                {
                    var submitJson = await submitResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!TryParseJson(submitJson, out var submitDoc) || submitDoc is null)
                        return new { cell = label, status = "submitFailed", preview = Preview(submitJson) };

                    using (submitDoc)
                    {
                        if (submitDoc.RootElement.TryGetProperty("error", out var submitError))
                            return new { cell = label, status = "submitFailed", error = submitError.Clone() };

                        if (!submitDoc.RootElement.TryGetProperty("jobId", out var jobIdElement))
                            return new { cell = label, status = "submitFailed", error = "No jobId returned." };

                        jobId = jobIdElement.GetString();
                    }
                }

                var jobUrl = $"{taskUrl.TrimEnd('/')}/jobs/{jobId}";
                var jobStatus = "esriJobSubmitted";
                var messages = new List<string>();

                for (var poll = 0; poll < JobPollLimit; poll++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), jobToken).ConfigureAwait(false);

                    using var statusResponse = await client
                        .GetAsync($"{jobUrl}?f=json&token={Uri.EscapeDataString(token)}", jobToken)
                        .ConfigureAwait(false);
                    var statusJson = await statusResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!TryParseJson(statusJson, out var statusDoc) || statusDoc is null) continue;

                    using (statusDoc)
                    {
                        if (statusDoc.RootElement.TryGetProperty("jobStatus", out var statusElement))
                            jobStatus = statusElement.GetString() ?? jobStatus;

                        messages.Clear();
                        if (statusDoc.RootElement.TryGetProperty("messages", out var messageArray))
                        {
                            foreach (var message in messageArray.EnumerateArray())
                            {
                                if (message.TryGetProperty("description", out var description))
                                    messages.Add(description.GetString() ?? string.Empty);
                            }
                        }
                    }

                    if (jobStatus is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled" or "esriJobTimedOut")
                        break;
                }

                if (jobStatus != "esriJobSucceeded")
                {
                    return new
                    {
                        cell = label,
                        status = jobStatus,
                        jobId,
                        messages,
                        seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                    };
                }

                string? outputUrl = null;
                using (var resultResponse = await client
                    .GetAsync($"{jobUrl}/results/Output_File?f=json&token={Uri.EscapeDataString(token)}", jobToken)
                    .ConfigureAwait(false))
                {
                    var resultJson = await resultResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (TryParseJson(resultJson, out var resultDoc) && resultDoc is not null)
                    {
                        using (resultDoc)
                        {
                            if (resultDoc.RootElement.TryGetProperty("value", out var value) &&
                                value.TryGetProperty("url", out var urlElement))
                            {
                                outputUrl = urlElement.GetString();
                            }
                        }
                    }
                }

                return new
                {
                    cell = label,
                    status = "esriJobSucceeded",
                    jobId,
                    outputUrl,
                    seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                };
            }
            catch (OperationCanceledException)
            {
                return new
                {
                    cell = label,
                    status = "timedOut",
                    jobId,
                    seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
                };
            }
        }

        private async Task<(string? TaskUrl, object? Error)> ResolveTaskUrlAsync(
            HttpClient client,
            string token,
            string? requestedTaskUrl)
        {
            if (!string.IsNullOrWhiteSpace(requestedTaskUrl))
                return (requestedTaskUrl, null);

            var itemUrl = $"{PortalRestUrl}/content/items/{CustomPrintItemId}?f=json&token={Uri.EscapeDataString(token)}";
            using var itemResponse = await client.GetAsync(itemUrl, HttpContext.RequestAborted).ConfigureAwait(false);
            var itemJson = await itemResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!TryParseJson(itemJson, out var itemDoc) || itemDoc is null)
                return (null, new { stage = "printItem", preview = Preview(itemJson) });

            using (itemDoc)
            {
                if (itemDoc.RootElement.TryGetProperty("error", out var itemError))
                    return (null, new { stage = "printItem", error = itemError.Clone() });

                if (!itemDoc.RootElement.TryGetProperty("url", out var serviceUrl))
                    return (null, new { stage = "printItem", error = "Print item has no service url." });

                var value = serviceUrl.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    return (null, new { stage = "printItem", error = "Print item service url was empty." });

                return (value.TrimEnd('/') + "/Export%20Web%20Map", null);
            }
        }

        private HttpClient CreateClient()
        {
            var client = _httpClientFactory.CreateClient("ArcGIS");
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        }

        private static string BuildWhere(string workOrderId)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "workorderid = '{0}'",
                workOrderId.Replace("'", "''", StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------
        private static bool SegmentsIntersectCell(
            List<(double X1, double Y1, double X2, double Y2)> segments,
            GridCell cell,
            double bufferFeet)
        {
            var xmin = cell.XMin - bufferFeet;
            var xmax = cell.XMax + bufferFeet;
            var ymin = cell.YMin - bufferFeet;
            var ymax = cell.YMax + bufferFeet;

            foreach (var segment in segments)
            {
                if ((segment.X1 >= xmin && segment.X1 <= xmax && segment.Y1 >= ymin && segment.Y1 <= ymax) ||
                    (segment.X2 >= xmin && segment.X2 <= xmax && segment.Y2 >= ymin && segment.Y2 <= ymax))
                {
                    return true;
                }

                var dx = segment.X2 - segment.X1;
                var dy = segment.Y2 - segment.Y1;
                double t0 = 0;
                double t1 = 1;
                var clipped = true;

                var p = new[] { -dx, dx, -dy, dy };
                var q = new[]
                {
                    segment.X1 - xmin,
                    xmax - segment.X1,
                    segment.Y1 - ymin,
                    ymax - segment.Y1,
                };

                for (var i = 0; i < 4; i++)
                {
                    if (Math.Abs(p[i]) < 1e-9)
                    {
                        if (q[i] < 0)
                        {
                            clipped = false;
                            break;
                        }

                        continue;
                    }

                    var r = q[i] / p[i];
                    if (p[i] < 0)
                    {
                        if (r > t1)
                        {
                            clipped = false;
                            break;
                        }

                        if (r > t0) t0 = r;
                    }
                    else
                    {
                        if (r < t0)
                        {
                            clipped = false;
                            break;
                        }

                        if (r < t1) t1 = r;
                    }
                }

                if (clipped && t0 <= t1) return true;
            }

            return false;
        }

        private static bool TryParseJson(string content, out JsonDocument? document)
        {
            document = null;
            if (string.IsNullOrWhiteSpace(content)) return false;

            var trimmed = content.TrimStart();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal) &&
                !trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                document = JsonDocument.Parse(content);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string Preview(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;

            var collapsed = content.Replace("\r", " ", StringComparison.Ordinal)
                                   .Replace("\n", " ", StringComparison.Ordinal);
            return collapsed.Length <= 600 ? collapsed : collapsed.Substring(0, 600);
        }
    }
}
