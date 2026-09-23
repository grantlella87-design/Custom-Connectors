# Custom Connectors

Power Platform custom connectors repository: **National Grid GIS Enhanced V13 Print**, a CDP-enhanced
(tabular) connector over National Grid GIS data with geoprocessing, project-map grid/export, spatial query
and forty-scale CustomPrint PDF actions. Protocol docs: https://aka.ms/PowerFxCDP

## Environment

National Grid New England Gas Workflow

Environment ID:
974a4409-f22c-e0f8-a512-9f4294f8d209

## Layout

- `CdpSampleWebApi/` - ASP.NET Core Web API implementing the CDP protocol and the connector actions:
  table query, GIS lookups, geoprocessing (`gis/geoprocessing`), project maps (`gis/maps/grid`,
  `gis/maps/export-page`), spatial query (`gis/spatial-query`) and print (`gis/print/generateMapPdf`,
  `gis/print/layout-info`). See its [readme](CdpSampleWebApi/readme.md).
- `CdpHelpers/` - shared library the Web API builds on (Power Fx `RecordType` <-> CDP protocol).
- `PowerPlatformArtifacts/` - connector definition (`apiDefinition.swagger.json`, `apiProperties.json`,
  `settings.json`) used to register the V13 connector with Power Platform.
- `Tests/` - unit tests for `CdpHelpers` and for the V13 map/print/spatial-query logic.
- `PowerFxBuild.props` - shared Power Fx / OData package versions imported by both projects.

## Build and run

Requires the .NET SDK pinned in `global.json` (8.0.425).

```
dotnet build CdpSampleWebApi.sln
dotnet run --project CdpSampleWebApi
dotnet test CdpSampleWebApi.sln
```

The API listens on http://localhost:5008 by default; `GET /$health` confirms it is running.

Publish for Windows / IIS or a Windows service:

```
dotnet publish CdpSampleWebApi -c Release -r win-x64 -o publish/win-x64
```

## Registering the connector

`PowerPlatformArtifacts/settings.json` is a `paconn` settings file; see
[PowerPlatformArtifacts/readme.md](PowerPlatformArtifacts/readme.md) for creating or updating the V13 connector.

The API signs in to ArcGIS Enterprise interactively (OAuth, token cached with Windows DPAPI), so run it on
the Windows gateway host.
