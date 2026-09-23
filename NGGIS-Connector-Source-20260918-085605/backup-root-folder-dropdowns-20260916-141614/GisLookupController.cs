// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Text;
using System.Text.Json;
using CdpSampleWebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace CdpSampleWebApi.Controllers
{
    /// <summary>
    /// Provides narrow ArcGIS catalog lookups for cascading connector dropdowns.
    /// </summary>
    [ApiController]
    [Route("gis/lookups")]
    public sealed class GisLookupController : ControllerBase
    {
        private const string BaseUrl = "https://gis.nationalgrid.com";
        private static readonly string[] Adaptors = { "arcgis", "dnv", "gp", "hosting", "lemurgis", "un" };
        private readonly HttpClient _http;
        private readonly ArcGisOAuthTokenProvider _tokenProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="GisLookupController"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory.</param>
        /// <param name="tokenProvider">The ArcGIS OAuth token provider.</param>
        public GisLookupController(IHttpClientFactory httpClientFactory, ArcGisOAuthTokenProvider tokenProvider)
        {
            _http = httpClientFactory.CreateClient("ArcGIS");
            _tokenProvider = tokenProvider;
        }

        /// <summary>
        /// Lists immediate ArcGIS folders without traversing services or layers.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The available folder selections.</returns>
        [HttpGet("folders")]
        public async Task<LookupResponse> GetFolders(CancellationToken cancellationToken)
        {
            var values = new List<LookupItem>();
            foreach (var adaptor in Adaptors)
            {
                using var json = await GetJsonAsync($"{BaseUrl}/{adaptor}/rest/services", cancellationToken).ConfigureAwait(false);
                values.Add(new LookupItem($"{adaptor}|", $"{adaptor} / Root"));
                if (!json.RootElement.TryGetProperty("folders", out var folders))
                {
                    continue;
                }

                foreach (var folderValue in folders.EnumerateArray())
                {
                    var folder = folderValue.GetString();
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        values.Add(new LookupItem($"{adaptor}|{folder}", folder));
                    }
                }
            }

            return new LookupResponse(values.OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        /// <summary>
        /// Lists services from only the selected ArcGIS folder.
        /// </summary>
        /// <param name="folder">The adaptor and folder selection.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The available service selections.</returns>
        [HttpGet("services")]
        public async Task<LookupResponse> GetServices([FromQuery] string folder, CancellationToken cancellationToken)
        {
            var (adaptor, folderPath) = ParseSelection(folder, "folder");
            ValidateAdaptor(adaptor);
            var suffix = string.IsNullOrWhiteSpace(folderPath) ? string.Empty : $"/{folderPath}";
            using var json = await GetJsonAsync($"{BaseUrl}/{adaptor}/rest/services{suffix}", cancellationToken).ConfigureAwait(false);
            var values = new List<LookupItem>();
            if (json.RootElement.TryGetProperty("services", out var services))
            {
                foreach (var service in services.EnumerateArray())
                {
                    var name = service.GetProperty("name").GetString();
                    var type = service.GetProperty("type").GetString();
                    if (string.IsNullOrWhiteSpace(name) || (type != "MapServer" && type != "FeatureServer"))
                    {
                        continue;
                    }

                    var displayName = name.Split('/').Last();
                    values.Add(new LookupItem($"{name}|{type}", displayName));
                }
            }

            return new LookupResponse(values.OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
        }

        /// <summary>
        /// Lists layers and tables from only the selected ArcGIS service.
        /// </summary>
        /// <param name="folder">The adaptor and folder selection.</param>
        /// <param name="service">The service name and type selection.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The available table selections.</returns>
        [HttpGet("tables")]
        public async Task<LookupResponse> GetTables([FromQuery] string folder, [FromQuery] string service, CancellationToken cancellationToken)
        {
            var (adaptor, _) = ParseSelection(folder, "folder");
            var (serviceName, serviceType) = ParseSelection(service, "service");
            ValidateAdaptor(adaptor);
            if (serviceType != "MapServer" && serviceType != "FeatureServer")
            {
                throw new ArgumentException("Invalid ArcGIS service type.", nameof(service));
            }

            var serviceUrl = $"{BaseUrl}/{adaptor}/rest/services/{serviceName}/{serviceType}";
            using var json = await GetJsonAsync(serviceUrl, cancellationToken).ConfigureAwait(false);
            var values = new List<LookupItem>();
            AddCollection("layers");
            AddCollection("tables");
            return new LookupResponse(values.OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());

            void AddCollection(string propertyName)
            {
                if (!json.RootElement.TryGetProperty(propertyName, out var items))
                {
                    return;
                }

                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("subLayerIds", out var childIds) && childIds.ValueKind == JsonValueKind.Array)
                    {
                        continue;
                    }

                    var id = item.GetProperty("id").GetInt32();
                    var displayName = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : id.ToString();
                    values.Add(new LookupItem(EncodeTableName($"{serviceUrl}/{id}"), displayName ?? id.ToString()));
                }
            }
        }

        private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            using var response = await _http.GetAsync($"{url}{separator}f=json&token={Uri.EscapeDataString(token)}", cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                document.Dispose();
                throw new InvalidOperationException($"ArcGIS catalog error: {error}");
            }

            return document;
        }

        private static (string Left, string Right) ParseSelection(string selection, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(selection))
            {
                throw new ArgumentException("A selection is required.", parameterName);
            }

            var separator = selection.LastIndexOf('|');
            if (separator < 0)
            {
                throw new ArgumentException("The selection is invalid.", parameterName);
            }

            return (selection[..separator], selection[(separator + 1) ..]);
        }

        private static void ValidateAdaptor(string adaptor)
        {
            if (!Adaptors.Contains(adaptor, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The ArcGIS adaptor is invalid.", nameof(adaptor));
            }
        }

        private static string EncodeTableName(string url)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Represents a lookup response for a connector dropdown.
        /// </summary>
        /// <param name="Value">The available values.</param>
        public sealed record LookupResponse(LookupItem[] Value);

        /// <summary>
        /// Represents a value and its user-facing display name.
        /// </summary>
        /// <param name="Value">The submitted value.</param>
        /// <param name="DisplayName">The displayed value.</param>
        public sealed record LookupItem(string Value, string DisplayName);
    }
}
