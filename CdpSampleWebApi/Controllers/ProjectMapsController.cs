using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Mvc;

namespace CdpSampleWebApi
{
    /// <summary>
    /// Project-map grid and page export endpoints (V13).
    /// </summary>
    [ApiController]
    [Route("gis/maps")]
    public sealed class ProjectMapsController : ControllerBase
    {
        private static readonly HashSet<string> ImageFormats = new (StringComparer.OrdinalIgnoreCase) { "png32", "png24", "png8", "jpg" };
        private static readonly Regex MapServicePattern = new ("^[A-Za-z0-9_\\-]+(/[A-Za-z0-9_\\-]+)*/MapServer$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex UnsafeFileNameCharacters = new ("[^A-Za-z0-9_\\-.]+", RegexOptions.CultureInvariant);
        private readonly ArcGisRestClient _arcGis;

        public ProjectMapsController(ArcGisRestClient arcGis)
        {
            _arcGis = arcGis;
        }

        [HttpPost("grid")]
        public IActionResult CreateGrid([FromBody] ProjectMapGrid.GridRequest request)
        {
            try
            {
                return Ok(ProjectMapGrid.Create(request));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("export-page")]
        public async Task<IActionResult> ExportPage([FromBody] ExportPageRequest request, CancellationToken cancellationToken)
        {
            string url;
            string boundingBox;
            try
            {
                (url, boundingBox) = Validate(request);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }

            var parameters = new Dictionary<string, string>
            {
                ["bbox"] = boundingBox,
                ["bboxSR"] = request.BoundingBoxSpatialReference.ToString(CultureInfo.InvariantCulture),
                ["imageSR"] = request.OutputSpatialReference.ToString(CultureInfo.InvariantCulture),
                ["size"] = request.WidthPixels.ToString(CultureInfo.InvariantCulture) + "," + request.HeightPixels.ToString(CultureInfo.InvariantCulture),
                ["dpi"] = request.Dpi.ToString(CultureInfo.InvariantCulture),
                ["format"] = request.ImageFormat.ToLowerInvariant(),
                ["transparent"] = request.Transparent ? "true" : "false",
            };
            if (!string.IsNullOrWhiteSpace(request.LayerDefinitionsJson))
            {
                parameters["layerDefs"] = request.LayerDefinitionsJson;
            }

            var (bytes, contentType) = await _arcGis.PostForImageAsync(url, parameters, cancellationToken).ConfigureAwait(false);
            var pageName = string.IsNullOrWhiteSpace(request.PageName) ? "Page" + request.PageNumber.ToString(CultureInfo.InvariantCulture) : request.PageName;
            return Ok(new ExportPageResponse
            {
                PageNumber = request.PageNumber,
                PageName = pageName,
                FileName = BuildFileName(request.WorkOrder, pageName, contentType),
                ContentType = contentType,
                ImageBase64 = Convert.ToBase64String(bytes),
                BoundingBox = boundingBox,
                BoundingBoxSpatialReference = request.BoundingBoxSpatialReference,
                OutputSpatialReference = request.OutputSpatialReference,
                WidthPixels = request.WidthPixels,
                HeightPixels = request.HeightPixels,
                Dpi = request.Dpi,
            });
        }

        internal static (string Url, string BoundingBox) Validate(ExportPageRequest request)
        {
            if (request == null)
            {
                throw new ArgumentException("A request body is required.");
            }

            var root = string.IsNullOrWhiteSpace(request.Root) ? "arcgis" : request.Root.Trim();
            ArcGisRestClient.ValidateAdaptor(root);
            var mapService = (request.MapService ?? string.Empty).Trim().Trim('/');
            if (!MapServicePattern.IsMatch(mapService))
            {
                throw new ArgumentException("Map service must be a relative ArcGIS MapServer path such as MA/Material_View_MA/MapServer.");
            }

            var parts = (request.BoundingBox ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
            var values = new double[4];
            if (parts.Length != 4 || !parts.Select((p, i) => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])).All(ok => ok)
                || values[0] >= values[2] || values[1] >= values[3])
            {
                throw new ArgumentException("Bounding box must be xmin,ymin,xmax,ymax with xmin < xmax and ymin < ymax.");
            }

            if (request.WidthPixels is < 1 or > 10000 || request.HeightPixels is < 1 or > 10000)
            {
                throw new ArgumentException("Width and height must be between 1 and 10000 pixels.");
            }

            if (request.Dpi is < 1 or > 1200)
            {
                throw new ArgumentException("DPI must be between 1 and 1200.");
            }

            if (!ImageFormats.Contains(request.ImageFormat ?? string.Empty))
            {
                throw new ArgumentException("Image format must be png32, png24, png8 or jpg.");
            }

            var boundingBox = string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            return ($"{ArcGisRestClient.BaseUrl}/{root}/rest/services/{mapService}/export", boundingBox);
        }

        internal static string BuildFileName(string workOrder, string pageName, string contentType)
        {
            var extension = contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || contentType.Contains("jpg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";
            var parts = new[] { workOrder, pageName }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => UnsafeFileNameCharacters.Replace(p.Trim(), "_").Trim('_'))
                .Where(p => p.Length > 0);
            return string.Join("_", parts) + extension;
        }

        public sealed class ExportPageRequest
        {
            public string Root { get; set; } = "arcgis";

            public string MapService { get; set; } = "MA/Material_View_MA/MapServer";

            public string BoundingBox { get; set; }

            public int BoundingBoxSpatialReference { get; set; } = 6492;

            public int OutputSpatialReference { get; set; } = 6492;

            public int WidthPixels { get; set; } = 3000;

            public int HeightPixels { get; set; } = 1600;

            public int Dpi { get; set; } = 200;

            public string ImageFormat { get; set; } = "png32";

            public bool Transparent { get; set; }

            public string LayerDefinitionsJson { get; set; }

            public string WorkOrder { get; set; }

            public int PageNumber { get; set; } = 1;

            public string PageName { get; set; }
        }

        public sealed class ExportPageResponse
        {
            public int PageNumber { get; set; }

            public string PageName { get; set; }

            public string FileName { get; set; }

            public string ContentType { get; set; }

            public string ImageBase64 { get; set; }

            public string BoundingBox { get; set; }

            public int BoundingBoxSpatialReference { get; set; }

            public int OutputSpatialReference { get; set; }

            public int WidthPixels { get; set; }

            public int HeightPixels { get; set; }

            public int Dpi { get; set; }
        }
    }
}
