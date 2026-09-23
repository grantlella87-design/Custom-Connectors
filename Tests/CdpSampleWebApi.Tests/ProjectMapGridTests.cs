using CdpSampleWebApi;

namespace CdpSampleWebApi.Tests
{
    public class ProjectMapGridTests
    {
        // At 1" = 40' (480) a 15" x 8" map frame covers 600' x 320' in a US-feet coordinate system.
        private static ProjectMapGrid.GridRequest Request(string geometryJson) => new ()
        {
            ProposedMainGeometryJson = geometryJson,
            InputSpatialReference = 6492,
            ScaleDenominator = 480,
            MapFrameWidthInches = 15,
            MapFrameHeightInches = 8,
        };

        [Fact]
        public void FortyScalePageSizeIsInFeet()
        {
            var result = ProjectMapGrid.Create(Request("{\"paths\":[[[0,0],[100,0]]]}"));

            Assert.Equal("US feet", result.Units);
            Assert.Equal(600, result.PageWidth, 6);
            Assert.Equal(320, result.PageHeight, 6);
            Assert.Equal(1, result.PageCount);
        }

        [Fact]
        public void StraightLineIsCoveredLeftToRightAndCentered()
        {
            var request = Request("{\"paths\":[[[0,0],[1500,0]]]}");
            request.PageNumberPrefix = "P";

            var result = ProjectMapGrid.Create(request);

            Assert.Equal(3, result.Columns);
            Assert.Equal(1, result.Rows);
            Assert.Equal(new[] { "P1", "P2", "P3" }, result.Pages.Select(p => p.PageName));
            Assert.Equal("-150,-160,450,160", result.Pages[0].BoundingBox);
            Assert.Equal("1050,-160,1650,160", result.Pages[2].BoundingBox);
            Assert.Contains("\"wkid\":6492", result.Pages[0].GeometryJson);
        }

        [Fact]
        public void PagesThatMissTheGeometryAreSkipped()
        {
            // An L-shaped main: along the bottom, then up the right side.
            var result = ProjectMapGrid.Create(Request("{\"paths\":[[[0,0],[1500,0],[1500,1000]]]}"));

            Assert.Equal(3, result.Columns);
            Assert.Equal(4, result.Rows);
            Assert.Equal(6, result.PageCount);
            Assert.Equal(Enumerable.Range(1, 6), result.Pages.Select(p => p.PageNumber));
        }

        [Fact]
        public void PolygonInteriorPagesAreKept()
        {
            var result = ProjectMapGrid.Create(Request("{\"rings\":[[[0,0],[0,3000],[3000,3000],[3000,0],[0,0]]]}"));

            Assert.Equal(result.Rows * result.Columns, result.PageCount);
        }

        [Fact]
        public void OverlapShortensTheStep()
        {
            var request = Request("{\"paths\":[[[0,0],[1500,0]]]}");
            request.PageOverlapPercent = 10;
            request.StartingPageNumber = 5;

            var result = ProjectMapGrid.Create(request);

            Assert.Equal(540, result.Pages[1].XMin - result.Pages[0].XMin, 6);
            Assert.Equal(5, result.Pages[0].PageNumber);
        }

        [Fact]
        public void MeterCoordinateSystemsUseMeters()
        {
            var request = Request("{\"x\":10,\"y\":10}");
            request.InputSpatialReference = 3857;

            var result = ProjectMapGrid.Create(request);

            Assert.Equal("meters", result.Units);
            Assert.Equal(15 * 480 * 0.0254, result.PageWidth, 6);
        }

        [Fact]
        public void GeometrySpatialReferenceWinsOverParameter()
        {
            var request = Request("{\"paths\":[[[0,0],[10,0]]],\"spatialReference\":{\"wkid\":3857}}");

            Assert.Equal(3857, ProjectMapGrid.Create(request).SpatialReference);
        }

        [Fact]
        public void AcceptsFeatureSetsAndGeometryCollections()
        {
            var featureSet = "{\"features\":[{\"geometry\":{\"paths\":[[[0,0],[1500,0]]]}}]}";
            var collection = "{\"geometryType\":\"esriGeometryPolyline\",\"geometries\":[{\"paths\":[[[0,0],[1500,0]]]}]}";

            Assert.Equal(3, ProjectMapGrid.Create(Request(featureSet)).PageCount);
            Assert.Equal(3, ProjectMapGrid.Create(Request(collection)).PageCount);
        }

        [Theory]
        [InlineData("{\"paths\":[[[0,0],[1,1]]],\"spatialReference\":{\"wkid\":4326}}")]
        [InlineData("not json")]
        [InlineData("{\"paths\":[]}")]
        public void InvalidInputIsRejected(string geometryJson)
        {
            Assert.Throws<ArgumentException>(() => ProjectMapGrid.Create(Request(geometryJson)));
        }
    }
}
