using System.Text;
using System.Text.Json;

namespace CdpSampleWebApi
{
    /// <summary>
    /// Raised when National Grid ArcGIS Enterprise returns an error or an unexpected response.
    /// </summary>
    public sealed class ArcGisException : Exception
    {
        public ArcGisException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Small helper for authenticated ArcGIS Enterprise REST calls used by the V13 map, print and spatial-query endpoints.
    /// </summary>
    public sealed class ArcGisRestClient
    {
        public const string BaseUrl = "https://gis.nationalgrid.com";
        public static readonly string[] Adaptors = { "arcgis", "dnv", "gp", "hosting", "lemurgis", "un" };

        private readonly HttpClient _http;
        private readonly ArcGisOAuthTokenProvider _tokenProvider;

        public ArcGisRestClient(IHttpClientFactory factory, ArcGisOAuthTokenProvider tokenProvider)
        {
            _http = factory.CreateClient("ArcGIS");
            _tokenProvider = tokenProvider;
        }

        /// <summary>
        /// GETs a REST resource as JSON. Throws <see cref="ArcGisException"/> when ArcGIS reports an error.
        /// </summary>
        public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var separator = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            using var response = await _http.GetAsync(url + separator + "f=json&token=" + Uri.EscapeDataString(token), cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// POSTs form parameters (plus f=json and the token) and returns the JSON response.
        /// Throws <see cref="ArcGisException"/> when ArcGIS reports an error.
        /// </summary>
        public async Task<JsonDocument> PostJsonAsync(string url, IDictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            var form = await BuildFormAsync(parameters, "json", cancellationToken).ConfigureAwait(false);
            using var response = await _http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
            return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// POSTs form parameters with f=image and returns the rendered image bytes.
        /// </summary>
        public async Task<(byte[] Bytes, string ContentType)> PostForImageAsync(string url, IDictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            var form = await BuildFormAsync(parameters, "image", cancellationToken).ConfigureAwait(false);
            using var response = await _http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (response.IsSuccessStatusCode && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return (await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), contentType);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ArcGisException($"ArcGIS export failed ({(int)response.StatusCode}): {Truncate(text)}");
        }

        /// <summary>
        /// Decodes a base64url layer identifier (as produced by the lookup endpoints) into a National Grid layer URL.
        /// </summary>
        public static string DecodeLayerUrl(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded))
            {
                throw new ArgumentException("A layer is required.");
            }

            string url;
            try
            {
                var value = encoded.Trim().Replace('-', '+').Replace('_', '/');
                value = value.PadRight(value.Length + ((4 - (value.Length % 4)) % 4), '=');
                url = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
                throw new ArgumentException("The layer identifier is not valid base64url.");
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !url.StartsWith(BaseUrl + "/", StringComparison.OrdinalIgnoreCase)
                || !(uri.AbsolutePath.Contains("/MapServer/", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("/FeatureServer/", StringComparison.OrdinalIgnoreCase))
                || !int.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out _))
            {
                throw new ArgumentException("Only National Grid ArcGIS MapServer or FeatureServer layers are allowed.");
            }

            return url.TrimEnd('/');
        }

        /// <summary>
        /// Ensures a URL points at a National Grid ArcGIS Enterprise GPServer task.
        /// </summary>
        public static string ValidateGpTaskUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !url.StartsWith(BaseUrl + "/", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.Contains("/rest/services/", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.Contains("/GPServer/", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Only National Grid ArcGIS Enterprise GPServer tasks are allowed.");
            }

            return url.TrimEnd('/');
        }

        public static void ValidateAdaptor(string root)
        {
            if (!Adaptors.Contains(root, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The ArcGIS adaptor is invalid.");
            }
        }

        private async Task<Dictionary<string, string>> BuildFormAsync(IDictionary<string, string> parameters, string format, CancellationToken cancellationToken)
        {
            var form = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parameters)
            {
                if (pair.Value != null)
                {
                    form[pair.Key] = pair.Value;
                }
            }

            form["f"] = format;
            form["token"] = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return form;
        }

        private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ArcGisException($"ArcGIS request failed ({(int)response.StatusCode}): {Truncate(text)}");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                throw new ArcGisException("ArcGIS returned a non-JSON response: " + Truncate(text));
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            {
                document.Dispose();
                throw new ArcGisException("ArcGIS error: " + error.GetRawText());
            }

            return document;
        }

        private static string Truncate(string text) => text == null || text.Length <= 500 ? text : text[..500] + "...";
    }
}
