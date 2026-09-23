using System.Text;
using System.Text.Json;
using CdpSampleWebApi;

namespace CdpSampleWebApi.Tests
{
    public class V13EndpointTests
    {
        private static string Encode(string url) => Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        [Fact]
        public void DecodesLookupLayerIdentifiers()
        {
            var url = "https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer/12";

            Assert.Equal(url, ArcGisRestClient.DecodeLayerUrl(Encode(url)));
        }

        [Theory]
        [InlineData("https://example.com/arcgis/rest/services/MA/X/MapServer/1")]
        [InlineData("https://gis.nationalgrid.com/arcgis/rest/services/MA/X/MapServer")]
        [InlineData("https://gis.nationalgrid.com/arcgis/rest/services/Tools/GPServer/Task")]
        public void RejectsNonLayerIdentifiers(string url)
        {
            Assert.Throws<ArgumentException>(() => ArcGisRestClient.DecodeLayerUrl(Encode(url)));
        }

        [Fact]
        public void DefaultPrintTasksAreAllowedGpTasks()
        {
            Assert.Equal(PrintController.DefaultExportWebMapTaskUrl, ArcGisRestClient.ValidateGpTaskUrl(PrintController.DefaultExportWebMapTaskUrl));
            Assert.Equal(PrintController.DefaultLayoutInfoTaskUrl, ArcGisRestClient.ValidateGpTaskUrl(PrintController.DefaultLayoutInfoTaskUrl));
            Assert.Throws<ArgumentException>(() => ArcGisRestClient.ValidateGpTaskUrl("https://example.com/gp/rest/services/CustomPrint/GPServer/Export"));
        }

        [Fact]
        public void ParsesProLayoutInfo()
        {
            using var json = JsonDocument.Parse("""
                [{"layoutTemplate":"Forty Scale Maps","pageSize":[17,11],"webMapFrameSize":[15,8],"pageUnits":"INCH","layoutOptions":{"hasLegend":true}}]
                """);

            var info = PrintController.ParseLayoutInfo("abc", json.RootElement);

            Assert.Equal("abc", info.LayoutItemId);
            Assert.Equal("Forty Scale Maps", info.LayoutTemplate);
            Assert.Equal(15, info.MapFrameWidth);
            Assert.Equal(8, info.MapFrameHeight);
            Assert.Equal(17, info.PageWidth);
            Assert.Equal(11, info.PageHeight);
            Assert.Equal("INCH", info.Units);
            Assert.True(info.HasLegend);
        }

        [Fact]
        public void ParsesSerializedLayoutInfoWithActiveDataFrame()
        {
            using var json = JsonDocument.Parse("\"{\\\"layoutTemplate\\\":\\\"L\\\",\\\"pageSize\\\":[11,8.5],\\\"activeDataFrameSize\\\":[10,6]}\"");

            var info = PrintController.ParseLayoutInfo("abc", json.RootElement);

            Assert.Equal(10, info.MapFrameWidth);
            Assert.Equal(6, info.MapFrameHeight);
            Assert.False(info.HasLegend);
        }

        [Fact]
        public void ExportPageBuildsMapServerExportUrl()
        {
            var request = new ProjectMapsController.ExportPageRequest { BoundingBox = " -150, -160 ,450,160 " };

            var (url, bbox) = ProjectMapsController.Validate(request);

            Assert.Equal("https://gis.nationalgrid.com/arcgis/rest/services/MA/Material_View_MA/MapServer/export", url);
            Assert.Equal("-150,-160,450,160", bbox);
        }

        [Theory]
        [InlineData("arcgis", "../secret/MapServer", "0,0,1,1")]
        [InlineData("arcgis", "MA/Material_View_MA/FeatureServer", "0,0,1,1")]
        [InlineData("nope", "MA/Material_View_MA/MapServer", "0,0,1,1")]
        [InlineData("arcgis", "MA/Material_View_MA/MapServer", "1,0,0,1")]
        [InlineData("arcgis", "MA/Material_View_MA/MapServer", "0,0,1")]
        public void ExportPageRejectsInvalidRequests(string root, string mapService, string bbox)
        {
            var request = new ProjectMapsController.ExportPageRequest { Root = root, MapService = mapService, BoundingBox = bbox };

            Assert.Throws<ArgumentException>(() => ProjectMapsController.Validate(request));
        }

        [Theory]
        [InlineData("WO 123/4", "P1", "image/png", "WO_123_4_P1.png")]
        [InlineData(null, "Page3", "image/jpeg", "Page3.jpg")]
        public void ExportFileNamesAreSafe(string workOrder, string pageName, string contentType, string expected)
        {
            Assert.Equal(expected, ProjectMapsController.BuildFileName(workOrder, pageName, contentType));
        }
    }
}
