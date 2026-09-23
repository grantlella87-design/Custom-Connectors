using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

namespace CdpSampleWebApi
{
    /// <summary>
    /// Spatial query against a National Grid ArcGIS layer (V13).
    /// </summary>
    [ApiController]
    [Route("gis/spatial-query")]
    public sealed class SpatialQueryController : ControllerBase
    {
        private const int MaxPages = 100;
        private static readonly HashSet<string> GeometryTypes = new (StringComparer.Ordinal)
        {
            "esriGeometryPoint", "esriGeometryMultipoint", "esriGeometryPolyline", "esriGeometryPolygon", "esriGeometryEnvelope",
        };

        private static readonly HashSet<string> SpatialRelationships = new (StringComparer.Ordinal)
        {
            "esriSpatialRelIntersects", "esriSpatialRelContains", "esriSpatialRelCrosses", "esriSpatialRelEnvelopeIntersects",
            "esriSpatialRelIndexIntersects", "esriSpatialRelOverlaps", "esriSpatialRelTouches", "esriSpatialRelWithin",
        };

        private readonly ArcGisRestClient _arcGis;

        public SpatialQueryController(ArcGisRestClient arcGis)
        {
            _arcGis = arcGis;
        }

        [HttpPost]
        public async Task<IActionResult> Query(
            [FromQuery] string tableName,
            [FromBody] SpatialQueryRequest request,
            [FromQuery] string filterQuery = "1=1",
            [FromQuery] string selectQuery = "*",
            [FromQuery] string geometryType = "esriGeometryPolyline",
            [FromQuery] string inputSpatialReference = "6492",
            [FromQuery] string spatialRelationship = "esriSpatialRelIntersects",
            [FromQuery] string outputSpatialReference = "6492",
            [FromQuery] bool returnGeometry = false,
            CancellationToken cancellationToken = default)
        {
            string layerUrl;
            try
            {
                layerUrl = ArcGisRestClient.DecodeLayerUrl(tableName);
                if (request == null || request.Geometry.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("Input geometry JSON must be an ArcGIS geometry object.");
                }

                if (!GeometryTypes.Contains(geometryType))
                {
                    throw new ArgumentException("Unsupported input geometry type.");
                }

                if (!SpatialRelationships.Contains(spatialRelationship))
                {
                    throw new ArgumentException("Unsupported spatial relationship.");
                }
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var parameters = new Dictionary<string, string>
            {
                ["where"] = string.IsNullOrWhiteSpace(filterQuery) ? "1=1" : filterQuery,
                ["outFields"] = string.IsNullOrWhiteSpace(selectQuery) ? "*" : selectQuery,
                ["geometry"] = request.Geometry.GetRawText(),
                ["geometryType"] = geometryType,
                ["inSR"] = inputSpatialReference,
                ["spatialRel"] = spatialRelationship,
                ["outSR"] = outputSpatialReference,
                ["returnGeometry"] = returnGeometry ? "true" : "false",
            };

            try
            {
                var rows = new List<Dictionary<string, object>>();
                var spatialReferenceWkid = outputSpatialReference;
                bool? supportsPagination = null;
                for (var page = 0; page < MaxPages; page++)
                {
                    if (rows.Count > 0)
                    {
                        parameters["resultOffset"] = rows.Count.ToString(CultureInfo.InvariantCulture);
                    }

                    using var json = await _arcGis.PostJsonAsync(layerUrl + "/query", parameters, cancellationToken).ConfigureAwait(false);
                    var root = json.RootElement;
                    spatialReferenceWkid = ReadWkid(root) ?? spatialReferenceWkid;
                    var added = 0;
                    if (root.TryGetProperty("features", out var features))
                    {
                        foreach (var feature in features.EnumerateArray())
                        {
                            rows.Add(ToRow(feature, returnGeometry));
                            added++;
                        }
                    }

                    var exceeded = root.TryGetProperty("exceededTransferLimit", out var limit) && limit.ValueKind == JsonValueKind.True;
                    if (!exceeded || added == 0)
                    {
                        break;
                    }

                    supportsPagination ??= await SupportsPaginationAsync(layerUrl, cancellationToken).ConfigureAwait(false);
                    if (supportsPagination == false)
                    {
                        break;
                    }
                }

                return Ok(new { value = rows, count = rows.Count, spatialReferenceWkid });
            }
            catch (Exception ex) when (ex is ArcGisException or HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
            }
        }

        private async Task<bool> SupportsPaginationAsync(string layerUrl, CancellationToken cancellationToken)
        {
            using var info = await _arcGis.GetJsonAsync(layerUrl, cancellationToken).ConfigureAwait(false);
            return info.RootElement.TryGetProperty("advancedQueryCapabilities", out var capabilities)
                && capabilities.TryGetProperty("supportsPagination", out var pagination)
                && pagination.ValueKind == JsonValueKind.True;
        }

        private static Dictionary<string, object> ToRow(JsonElement feature, bool returnGeometry)
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            if (feature.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
            {
                foreach (var attribute in attributes.EnumerateObject())
                {
                    row[attribute.Name] = attribute.Value.Clone();
                }
            }

            if (returnGeometry)
            {
                row["GeometryJson"] = feature.TryGetProperty("geometry", out var geometry) ? geometry.GetRawText() : null;
            }

            return row;
        }

        private static string ReadWkid(JsonElement root)
        {
            if (!root.TryGetProperty("spatialReference", out var sr))
            {
                return null;
            }

            if (sr.TryGetProperty("latestWkid", out var latest))
            {
                return latest.GetRawText();
            }

            return sr.TryGetProperty("wkid", out var wkid) ? wkid.GetRawText() : null;
        }

        public sealed class SpatialQueryRequest
        {
            public JsonElement Geometry { get; set; }
        }
    }
}
