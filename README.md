# Custom Connectors

Power Platform custom connectors repository: **National Grid GIS Enhanced V10**, a CDP-enhanced
(tabular) connector over National Grid GIS data. Protocol docs: https://aka.ms/PowerFxCDP

## Environment

National Grid New England Gas Workflow

Environment ID:
974a4409-f22c-e0f8-a512-9f4294f8d209

## Layout

- `CdpSampleWebApi/` - ASP.NET Core Web API implementing the CDP protocol (ArcGIS table provider,
  GIS lookup, geoprocessing and health endpoints). See its [readme](CdpSampleWebApi/readme.md).
- `CdpHelpers/` - shared library the Web API builds on (Power Fx `RecordType` <-> CDP protocol).
- `PowerPlatformArtifacts/` - connector definition (`apiDefinition.swagger.json`, `apiProperties.json`,
  `settings.json`) used to register the V10 connector with Power Platform.
- `Tests/CdpHelpers.Tests/` - unit tests for `CdpHelpers`.
- `PowerFxBuild.props` - shared Power Fx / OData package versions imported by both projects.

## Build and run

Requires the .NET SDK pinned in `global.json` (8.0.425).

```
dotnet build CdpSampleWebApi.sln
dotnet run --project CdpSampleWebApi
dotnet test Tests/CdpHelpers.Tests
```

The API listens on http://localhost:5008 by default; `GET /$health` confirms it is running.

Publish for Windows / IIS or a Windows service:

```
dotnet publish CdpSampleWebApi -c Release -r win-x64 -o publish/win-x64
```

## Registering the connector

`PowerPlatformArtifacts/settings.json` is a `paconn` settings file. From that folder, update the existing connector with:

```
paconn update --settings settings.json
```
