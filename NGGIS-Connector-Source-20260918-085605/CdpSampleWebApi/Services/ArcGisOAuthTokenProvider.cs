// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CdpSampleWebApi
{
    public sealed class ArcGisOAuthTokenProvider
    {
        private const string ClientId = "48XCGWtLoUxA3klq";
        private const string RedirectUri = "http://localhost:8080/";
        private const string AuthorizeEndpoint = "https://gis.nationalgrid.com/portal/sharing/rest/oauth2/authorize";
        private const string TokenEndpoint = "https://gis.nationalgrid.com/portal/sharing/rest/oauth2/token";
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly string _cachePath;
        private TokenState _state;

        public ArcGisOAuthTokenProvider(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient("ArcGISAuth");
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NG-GIS-Enhanced-Connector");
            Directory.CreateDirectory(folder);
            _cachePath = Path.Combine(folder, "oauth-cache.bin");
            _state = LoadState();
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_state != null && !string.IsNullOrWhiteSpace(_state.AccessToken) && _state.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2))
                {
                    return _state.AccessToken;
                }
                if (_state != null && !string.IsNullOrWhiteSpace(_state.RefreshToken))
                {
                    try
                    {
                        _state = await RefreshAsync(_state.RefreshToken, cancellationToken).ConfigureAwait(false);
                        SaveState(_state);
                        return _state.AccessToken;
                    }
                    catch
                    {
                        _state = null;
                        if (File.Exists(_cachePath))
                        {
                            File.Delete(_cachePath);
                        }
                    }
                }
                _state = await InteractiveAsync(cancellationToken).ConfigureAwait(false);
                SaveState(_state);
                return _state.AccessToken;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<TokenState> InteractiveAsync(CancellationToken cancellationToken)
        {
            var authorizeUrl = AuthorizeEndpoint + "?client_id=" + Uri.EscapeDataString(ClientId) + "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) + "&expiration=20160";
            using var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri);
            listener.Start();
            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];
            var responseText = string.IsNullOrWhiteSpace(code) ? "ArcGIS authorization failed. You may close this tab." : "ArcGIS authorization completed. You may close this tab.";
            var bytes = Encoding.UTF8.GetBytes(responseText);
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            context.Response.Close();
            listener.Stop();
            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("ArcGIS authorization did not return a code.");
            }
            return await ExchangeCodeAsync(code, cancellationToken).ConfigureAwait(false);
        }

        private Task<TokenState> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
        {
            return RequestTokenAsync(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["client_id"] = ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri
            }, null, cancellationToken);
        }

        private Task<TokenState> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            return RequestTokenAsync(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            }, refreshToken, cancellationToken);
        }

        private async Task<TokenState> RequestTokenAsync(Dictionary<string, string> form, string previousRefreshToken, CancellationToken cancellationToken)
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _httpClient.PostAsync(new Uri(TokenEndpoint), content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException("ArcGIS OAuth error: " + error);
            }
            var accessToken = document.RootElement.GetProperty("access_token").GetString();
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 1800;
            var refreshToken = document.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : previousRefreshToken;
            return new TokenState(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }

        private TokenState LoadState()
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }
            try
            {
                var protectedBytes = File.ReadAllBytes(_cachePath);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<TokenState>(bytes);
            }
            catch
            {
                return null;
            }
        }

        private void SaveState(TokenState state)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_cachePath, protectedBytes);
        }

        private sealed record TokenState(string AccessToken, string RefreshToken, DateTimeOffset ExpiresUtc);
    }
}
