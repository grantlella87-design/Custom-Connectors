using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

namespace CdpSampleWebApi
{
    [ApiController]
    [Route("gis/geoprocessing")]
    public sealed class GeoprocessingController : ControllerBase
    {
        private const string EnterprisePrefix = "https://gis.nationalgrid.com/";
        private const string GeometryRoot = "https://gis.nationalgrid.com/arcgis/rest/services/Utilities/Geometry/GeometryServer/";
        private static readonly HashSet<string> GeometryOperations = new (StringComparer.OrdinalIgnoreCase)
        {
            "areasAndLengths", "autoComplete", "buffer", "convexHull", "cut", "densify", "difference",
            "distance", "findTransformations", "fromGeoCoordinateString", "generalize", "intersect",
            "labelPoints", "lengths", "offset", "project", "relation", "reshape", "simplify",
            "toGeoCoordinateString", "trimExtend", "union"
        };
        private readonly HttpClient _http;
        private readonly ArcGisOAuthTokenProvider _tokenProvider;

        public GeoprocessingController(IHttpClientFactory factory, ArcGisOAuthTokenProvider tokenProvider)
        {
            _http = factory.CreateClient("ArcGIS");
            _tokenProvider = tokenProvider;
        }

        [HttpPost("geometry/{operation}")]
        public Task<IActionResult> Geometry(string operation, [FromBody] Dictionary<string, JsonElement>? parameters, CancellationToken cancellationToken)
        {
            if (!GeometryOperations.Contains(operation))
            {
                throw new InvalidOperationException("Unsupported ArcGIS Enterprise Geometry Service operation.");
            }
            return ForwardForm(GeometryRoot + operation, parameters, cancellationToken);
        }

        [HttpGet("gp/resource")]
        public Task<IActionResult> GetGpResource([FromQuery] string resourceUrl, CancellationToken cancellationToken)
        {
            return ForwardGet(ValidateGpUrl(resourceUrl), cancellationToken);
        }

        [HttpPost("gp/execute")]
        public Task<IActionResult> Execute([FromQuery] string taskUrl, [FromBody] Dictionary<string, JsonElement>? parameters, CancellationToken cancellationToken)
        {
            return ForwardForm(ValidateGpUrl(taskUrl).TrimEnd('/') + "/execute", parameters, cancellationToken);
        }

        [HttpPost("gp/submitJob")]
        public Task<IActionResult> SubmitJob([FromQuery] string taskUrl, [FromBody] Dictionary<string, JsonElement>? parameters, CancellationToken cancellationToken)
        {
            return ForwardForm(ValidateGpUrl(taskUrl).TrimEnd('/') + "/submitJob", parameters, cancellationToken);
        }

        [HttpGet("gp/job")]
        public Task<IActionResult> Job([FromQuery] string taskUrl, [FromQuery] string jobId, CancellationToken cancellationToken)
        {
            return ForwardGet(ValidateGpUrl(taskUrl).TrimEnd('/') + "/jobs/" + Uri.EscapeDataString(jobId), cancellationToken);
        }

        [HttpGet("gp/result")]
        public Task<IActionResult> Result([FromQuery] string taskUrl, [FromQuery] string jobId, [FromQuery] string resultName, CancellationToken cancellationToken)
        {
            return ForwardGet(ValidateGpUrl(taskUrl).TrimEnd('/') + "/jobs/" + Uri.EscapeDataString(jobId) + "/results/" + Uri.EscapeDataString(resultName), cancellationToken);
        }

        [HttpPost("gp/cancel")]
        public Task<IActionResult> Cancel([FromQuery] string taskUrl, [FromQuery] string jobId, CancellationToken cancellationToken)
        {
            return ForwardForm(ValidateGpUrl(taskUrl).TrimEnd('/') + "/jobs/" + Uri.EscapeDataString(jobId) + "/cancel", null, cancellationToken);
        }

        private async Task<IActionResult> ForwardGet(string url, CancellationToken cancellationToken)
        {
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var separator = url.Contains("?", StringComparison.Ordinal) ? "&" : "?";
            using var response = await _http.GetAsync(url + separator + "f=json&token=" + Uri.EscapeDataString(token), cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ContentResult { StatusCode = (int)response.StatusCode, ContentType = "application/json", Content = text };
        }

        private async Task<IActionResult> ForwardForm(string url, Dictionary<string, JsonElement>? parameters, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["f"] = "json" };
            if (parameters != null)
            {
                foreach (var pair in parameters)
                {
                    form[pair.Key] = pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() ?? string.Empty : pair.Value.GetRawText();
                }
            }
            form["token"] = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ContentResult { StatusCode = (int)response.StatusCode, ContentType = "application/json", Content = text };
        }

        private static string ValidateGpUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !url.StartsWith(EnterprisePrefix, StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.Contains("/rest/services/", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.Contains("/GPServer", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only National Grid ArcGIS Enterprise GPServer resources are allowed.");
            }
            return url;
        }
    }
}
