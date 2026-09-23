using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CdpSampleWebApi
{
    /// <summary>
    /// Builds fixed-scale project-map page rectangles that cover a proposed-main geometry.
    /// </summary>
    public static class ProjectMapGrid
    {
        // Projected coordinate systems whose linear unit is the US survey foot.
        private static readonly HashSet<int> UsFootWkids = new ()
        {
            6492, 6493, 6568, 6537, 6535, 6541, 6539,  // NAD83(2011) MA, RI, NY state plane
            2249, 2250, 3438, 2260, 2261, 2262, 2263,  // NAD83 MA, RI, NY state plane
            102686, 102687, 102730, 102715, 102716, 102717, 102718,
        };

        private static readonly HashSet<int> GeographicWkids = new () { 4326, 4269, 4283, 4258, 6318, 4152 };

        /// <summary>
        /// Creates the grid. Throws <see cref="ArgumentException"/> for invalid input.
        /// </summary>
        public static GridResult Create(GridRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProposedMainGeometryJson))
            {
                throw new ArgumentException("Proposed main geometry JSON is required.");
            }

            if (!(request.ScaleDenominator > 0) || !(request.MapFrameWidthInches > 0) || !(request.MapFrameHeightInches > 0))
            {
                throw new ArgumentException("Scale denominator and map-frame width/height must be positive.");
            }

            if (request.PageOverlapPercent < 0 || request.PageOverlapPercent >= 90)
            {
                throw new ArgumentException("Page overlap percent must be between 0 and 90.");
            }

            var shapes = GeometryShapes.Parse(request.ProposedMainGeometryJson);
            var wkid = shapes.Wkid ?? request.InputSpatialReference;
            if (GeographicWkids.Contains(wkid))
            {
                throw new ArgumentException($"Spatial reference {wkid} is geographic (degrees). Project the geometry to a projected coordinate system such as 6492 first.");
            }

            var usFeet = UsFootWkids.Contains(wkid);
            var unitsPerInch = usFeet ? 1.0 / 12.0 : 0.0254;
            var pageWidth = request.MapFrameWidthInches * request.ScaleDenominator * unitsPerInch;
            var pageHeight = request.MapFrameHeightInches * request.ScaleDenominator * unitsPerInch;
            var stepX = pageWidth * (1 - (request.PageOverlapPercent / 100));
            var stepY = pageHeight * (1 - (request.PageOverlapPercent / 100));

            var extent = shapes.Extent();
            var columns = CellCount(extent.XMax - extent.XMin, pageWidth, stepX);
            var rows = CellCount(extent.YMax - extent.YMin, pageHeight, stepY);

            // Center the grid on the geometry extent so the padding is even on every side.
            var gridWidth = pageWidth + ((columns - 1) * stepX);
            var gridHeight = pageHeight + ((rows - 1) * stepY);
            var originX = ((extent.XMin + extent.XMax) / 2) - (gridWidth / 2);
            var top = ((extent.YMin + extent.YMax) / 2) + (gridHeight / 2);

            var pages = new List<GridPage>();
            var pageNumber = request.StartingPageNumber;
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var xmin = originX + (column * stepX);
                    var ymax = top - (row * stepY);
                    var cell = new Envelope(xmin, ymax - pageHeight, xmin + pageWidth, ymax);
                    if (!shapes.Intersects(cell))
                    {
                        continue;
                    }

                    pages.Add(new GridPage
                    {
                        PageNumber = pageNumber,
                        PageName = (request.PageNumberPrefix ?? string.Empty) + pageNumber.ToString(CultureInfo.InvariantCulture),
                        Row = row + 1,
                        Column = column + 1,
                        XMin = cell.XMin,
                        YMin = cell.YMin,
                        XMax = cell.XMax,
                        YMax = cell.YMax,
                        BoundingBox = string.Join(",", new[] { cell.XMin, cell.YMin, cell.XMax, cell.YMax }.Select(Format)),
                        GeometryJson = cell.ToPolygonJson(wkid),
                    });
                    pageNumber++;
                }
            }

            return new GridResult
            {
                SpatialReference = wkid,
                Units = usFeet ? "US feet" : "meters",
                ScaleDenominator = request.ScaleDenominator,
                MapFrameWidthInches = request.MapFrameWidthInches,
                MapFrameHeightInches = request.MapFrameHeightInches,
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                PageOverlapPercent = request.PageOverlapPercent,
                Rows = rows,
                Columns = columns,
                PageCount = pages.Count,
                Extent = new ExtentInfo { XMin = extent.XMin, YMin = extent.YMin, XMax = extent.XMax, YMax = extent.YMax },
                Pages = pages,
            };
        }

        internal static string Format(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

        private static int CellCount(double extentSize, double pageSize, double step)
        {
            if (extentSize <= pageSize)
            {
                return 1;
            }

            return (int)Math.Ceiling(((extentSize - pageSize) / step) - 1e-9) + 1;
        }

        public sealed class GridRequest
        {
            public string ProposedMainGeometryJson { get; set; }

            public int InputSpatialReference { get; set; } = 6492;

            public double ScaleDenominator { get; set; } = 480;

            public double MapFrameWidthInches { get; set; } = 15;

            public double MapFrameHeightInches { get; set; } = 8;

            public double PageOverlapPercent { get; set; }

            public string PageNumberPrefix { get; set; } = string.Empty;

            public int StartingPageNumber { get; set; } = 1;
        }

        public sealed class GridResult
        {
            public int SpatialReference { get; set; }

            public string Units { get; set; }

            public double ScaleDenominator { get; set; }

            public double MapFrameWidthInches { get; set; }

            public double MapFrameHeightInches { get; set; }

            public double PageWidth { get; set; }

            public double PageHeight { get; set; }

            public double PageOverlapPercent { get; set; }

            public int Rows { get; set; }

            public int Columns { get; set; }

            public int PageCount { get; set; }

            public ExtentInfo Extent { get; set; }

            public List<GridPage> Pages { get; set; }
        }

        public sealed class ExtentInfo
        {
            [JsonPropertyName("xmin")]
            public double XMin { get; set; }

            [JsonPropertyName("ymin")]
            public double YMin { get; set; }

            [JsonPropertyName("xmax")]
            public double XMax { get; set; }

            [JsonPropertyName("ymax")]
            public double YMax { get; set; }
        }

        public sealed class GridPage
        {
            public int PageNumber { get; set; }

            public string PageName { get; set; }

            public int Row { get; set; }

            public int Column { get; set; }

            [JsonPropertyName("xmin")]
            public double XMin { get; set; }

            [JsonPropertyName("ymin")]
            public double YMin { get; set; }

            [JsonPropertyName("xmax")]
            public double XMax { get; set; }

            [JsonPropertyName("ymax")]
            public double YMax { get; set; }

            public string BoundingBox { get; set; }

            public string GeometryJson { get; set; }
        }

        internal readonly record struct Envelope(double XMin, double YMin, double XMax, double YMax)
        {
            public bool Contains(double x, double y) => x >= XMin && x <= XMax && y >= YMin && y <= YMax;

            public string ToPolygonJson(int wkid)
            {
                var ring = new[] { (XMin, YMin), (XMin, YMax), (XMax, YMax), (XMax, YMin), (XMin, YMin) };
                var coordinates = string.Join(",", ring.Select(p => "[" + Format(p.Item1) + "," + Format(p.Item2) + "]"));
                return "{\"rings\":[[" + coordinates + "]],\"spatialReference\":{\"wkid\":" + wkid.ToString(CultureInfo.InvariantCulture) + "}}";
            }
        }

        /// <summary>
        /// Flattened ArcGIS JSON geometry: line work (paths and rings), polygon rings and points.
        /// </summary>
        internal sealed class GeometryShapes
        {
            private readonly List<(double X, double Y)[]> _lines = new ();
            private readonly List<(double X, double Y)[]> _rings = new ();
            private readonly List<(double X, double Y)> _points = new ();

            public int? Wkid { get; private set; }

            /// <summary>
            /// Accepts a single ArcGIS geometry, a geometry collection ({geometryType, geometries}),
            /// a feature set ({features:[{geometry}]}) or a JSON array of geometries.
            /// </summary>
            public static GeometryShapes Parse(string json)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(json);
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException("Proposed main geometry JSON is not valid JSON: " + ex.Message);
                }

                using (document)
                {
                    var shapes = new GeometryShapes();
                    shapes.Add(document.RootElement);
                    if (shapes._lines.Count == 0 && shapes._points.Count == 0)
                    {
                        throw new ArgumentException("Proposed main geometry JSON contains no coordinates.");
                    }

                    return shapes;
                }
            }

            public Envelope Extent()
            {
                var all = _lines.SelectMany(l => l).Concat(_points).ToList();
                return new Envelope(all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y));
            }

            public bool Intersects(Envelope cell)
            {
                if (_points.Any(p => cell.Contains(p.X, p.Y)))
                {
                    return true;
                }

                foreach (var line in _lines)
                {
                    for (var i = 0; i < line.Length - 1; i++)
                    {
                        if (SegmentIntersects(cell, line[i], line[i + 1]))
                        {
                            return true;
                        }
                    }

                    if (line.Length == 1 && cell.Contains(line[0].X, line[0].Y))
                    {
                        return true;
                    }
                }

                // A page entirely inside a polygon touches no edge; test its center.
                var centerX = (cell.XMin + cell.XMax) / 2;
                var centerY = (cell.YMin + cell.YMax) / 2;
                return _rings.Count(ring => RingContains(ring, centerX, centerY)) % 2 == 1;
            }

            private void Add(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in element.EnumerateArray())
                    {
                        Add(item);
                    }

                    return;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                if (Wkid == null && element.TryGetProperty("spatialReference", out var sr))
                {
                    if (sr.TryGetProperty("latestWkid", out var latest) && latest.TryGetInt32(out var latestWkid))
                    {
                        Wkid = latestWkid;
                    }
                    else if (sr.TryGetProperty("wkid", out var wkid) && wkid.TryGetInt32(out var wkidValue))
                    {
                        Wkid = wkidValue;
                    }
                }

                if (element.TryGetProperty("geometries", out var geometries))
                {
                    Add(geometries);
                }

                if (element.TryGetProperty("features", out var features))
                {
                    foreach (var feature in features.EnumerateArray())
                    {
                        if (feature.TryGetProperty("geometry", out var geometry))
                        {
                            Add(geometry);
                        }
                    }
                }

                if (element.TryGetProperty("geometry", out var single) && single.ValueKind == JsonValueKind.Object)
                {
                    Add(single);
                }

                if (element.TryGetProperty("paths", out var paths))
                {
                    foreach (var path in paths.EnumerateArray())
                    {
                        _lines.Add(ReadCoordinates(path));
                    }
                }

                if (element.TryGetProperty("rings", out var rings))
                {
                    foreach (var ring in rings.EnumerateArray())
                    {
                        var coordinates = ReadCoordinates(ring);
                        _lines.Add(coordinates);
                        _rings.Add(coordinates);
                    }
                }

                if (element.TryGetProperty("points", out var points))
                {
                    _points.AddRange(ReadCoordinates(points));
                }

                if (element.TryGetProperty("x", out var x) && element.TryGetProperty("y", out var y) && x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number)
                {
                    _points.Add((x.GetDouble(), y.GetDouble()));
                }

                if (element.TryGetProperty("xmin", out var xmin) && element.TryGetProperty("ymin", out var ymin)
                    && element.TryGetProperty("xmax", out var xmax) && element.TryGetProperty("ymax", out var ymax))
                {
                    var box = new[] { (xmin.GetDouble(), ymin.GetDouble()), (xmin.GetDouble(), ymax.GetDouble()), (xmax.GetDouble(), ymax.GetDouble()), (xmax.GetDouble(), ymin.GetDouble()), (xmin.GetDouble(), ymin.GetDouble()) };
                    _lines.Add(box);
                    _rings.Add(box);
                }
            }

            private static (double X, double Y)[] ReadCoordinates(JsonElement array)
            {
                return array.EnumerateArray()
                    .Where(c => c.ValueKind == JsonValueKind.Array && c.GetArrayLength() >= 2)
                    .Select(c => (c[0].GetDouble(), c[1].GetDouble()))
                    .ToArray();
            }

            // Liang-Barsky clip: true when any part of the segment lies inside the envelope.
            private static bool SegmentIntersects(Envelope cell, (double X, double Y) a, (double X, double Y) b)
            {
                double t0 = 0, t1 = 1;
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var p = new[] { -dx, dx, -dy, dy };
                var q = new[] { a.X - cell.XMin, cell.XMax - a.X, a.Y - cell.YMin, cell.YMax - a.Y };
                for (var i = 0; i < 4; i++)
                {
                    if (p[i] == 0)
                    {
                        if (q[i] < 0)
                        {
                            return false;
                        }

                        continue;
                    }

                    var t = q[i] / p[i];
                    if (p[i] < 0)
                    {
                        t0 = Math.Max(t0, t);
                    }
                    else
                    {
                        t1 = Math.Min(t1, t);
                    }

                    if (t0 > t1)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool RingContains((double X, double Y)[] ring, double x, double y)
            {
                var inside = false;
                for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
                {
                    if (((ring[i].Y > y) != (ring[j].Y > y))
                        && (x < ((ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y)) + ring[i].X))
                    {
                        inside = !inside;
                    }
                }

                return inside;
            }
        }
    }
}
