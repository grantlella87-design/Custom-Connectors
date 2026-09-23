// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Text.Json;
using Microsoft.PowerFx;
using Microsoft.PowerFx.Connectors;
using Microsoft.PowerFx.Types;

namespace CdpSampleWebApi
{
    public sealed class ArcGisTableProviderFactory : ITableProviderFactory
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ArcGisOAuthTokenProvider _tokenProvider;

        public ArcGisTableProviderFactory(IHttpClientFactory httpClientFactory, ArcGisOAuthTokenProvider tokenProvider)
        {
            _httpClientFactory = httpClientFactory;
            _tokenProvider = tokenProvider;
        }

        public ITableProvider Get(IReadOnlyDictionary<string, string> settings)
        {
            return new ArcGisTableProvider(_httpClientFactory.CreateClient("ArcGIS"), _tokenProvider);
        }
    }

    public sealed class ArcGisTableProvider : ITableProvider
    {
        private const string DatasetName = "prod";
        private const string BaseUrl = "https://gis.nationalgrid.com";
        private static readonly string[] Adaptors = { "arcgis", "dnv", "gp", "hosting", "lemurgis", "un" };
        private readonly HttpClient _http;
        private readonly ArcGisOAuthTokenProvider _tokenProvider;

        public ArcGisTableProvider(HttpClient http, ArcGisOAuthTokenProvider tokenProvider)
        {
            _http = http;
            _tokenProvider = tokenProvider;
        }

        public Task<DatasetResponse.Item[]> GetDatasetsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new[]
            {
                new DatasetResponse.Item { Name = DatasetName, DisplayName = "National Grid GIS PROD" }
            });
        }

        public async Task<GetTablesResponse> GetTablesAsync(string dataset, CancellationToken cancellationToken = default)
        {
            ValidateDataset(dataset);
            var tables = new List<RawTablePoco>();
            foreach (var adaptor in Adaptors)
            {
                await DiscoverFolderAsync(adaptor, string.Empty, tables, cancellationToken).ConfigureAwait(false);
            }
            return new GetTablesResponse { Value = tables };
        }

        public async Task<RecordType> GetTableAsync(string dataset, string tableName, CancellationToken cancellationToken = default)
        {
            ValidateDataset(dataset);
            var layer = await GetLayerAsync(tableName, cancellationToken).ConfigureAwait(false);
            var record = RecordType.Empty();
            foreach (var field in layer.Fields)
            {
                record = record.Add(new NamedFormulaType(field.Name, GetFormulaType(field.Type)));
            }
            return record;
        }

        public Task<TableValue> GetTableValueAsync(string dataset, string tableName, CancellationToken cancellationToken = default)
        {
            return GetTableValueAsync(dataset, tableName, false, cancellationToken);
        }

        public async Task<TableValue> GetTableValueAsync(string dataset, string tableName, bool decodeDomainsAndSubtypes, CancellationToken cancellationToken = default)
        {
            ValidateDataset(dataset);
            var layer = await GetLayerAsync(tableName, cancellationToken).ConfigureAwait(false);
            var url = DecodeTableName(tableName) + "/query";
            var form = new Dictionary<string, string>
            {
                ["f"] = "json",
                ["where"] = "1=1",
                ["outFields"] = "*",
                ["returnGeometry"] = "false",
                ["resultRecordCount"] = "2000",
                ["token"] = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)
            };
            using var response = await _http.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            ThrowIfArcGisError(json.RootElement, url);
            var rows = new List<Dictionary<string, object?>>();
            if (json.RootElement.TryGetProperty("features", out var features))
            {
                foreach (var feature in features.EnumerateArray())
                {
                    var row = new Dictionary<string, object?>();
                    if (feature.TryGetProperty("attributes", out var attributes))
                    {
                        foreach (var field in layer.Fields)
                        {
                            row[field.Name] = attributes.TryGetProperty(field.Name, out var value)
                                ? ConvertJsonValue(value, field.Type)
                                : null;
                        }

                        if (decodeDomainsAndSubtypes)
                        {
                            foreach (var field in layer.Fields)
                            {
                                row.TryGetValue(field.Name, out var value);
                                row[field.Name] = DecodeDomainValue(layer, field, row, value);
                            }
                        }
                    }

                    rows.Add(row);
                }
            }
            var cache = new TypeMarshallerCache();
            var records = new List<RecordValue>();
            foreach (var row in rows)
            {
                var values = new List<NamedValue>();
                foreach (var field in layer.Fields)
                {
                    row.TryGetValue(field.Name, out var value);
                    FormulaValue formulaValue;
                    if (value == null)
                    {
                        var formulaType = field.Type switch
                        {
                            "esriFieldTypeDouble" => FormulaType.Number,
                            "esriFieldTypeSingle" => FormulaType.Number,
                            "esriFieldTypeInteger" => FormulaType.Number,
                            "esriFieldTypeSmallInteger" => FormulaType.Number,
                            "esriFieldTypeOID" => FormulaType.Number,
                            "esriFieldTypeDate" => FormulaType.DateTime,
                            _ => FormulaType.String,
                        };
                        formulaValue = FormulaValue.NewBlank(formulaType);
                    }
                    else
                    {
                        formulaValue = cache.Marshal(value, value.GetType());
                    }
                    values.Add(new NamedValue(field.Name, formulaValue));
                }
                records.Add(FormulaValue.NewRecordFromFields(values));
            }
            if (records.Count == 0)
            {
                return FormulaValue.NewTable(RecordType.Empty(), records);
            }
            return FormulaValue.NewTable((RecordType)records[0].Type, records);
        }

        private async Task DiscoverFolderAsync(string adaptor, string folder, List<RawTablePoco> tables, CancellationToken cancellationToken)
        {
            var catalogUrl = $"{BaseUrl}/{adaptor}/rest/services" + (string.IsNullOrWhiteSpace(folder) ? string.Empty : $"/{folder}");
            using var json = await GetJsonAsync(catalogUrl, cancellationToken).ConfigureAwait(false);
            if (json.RootElement.TryGetProperty("folders", out var folders))
            {
                foreach (var child in folders.EnumerateArray())
                {
                    var childName = child.GetString();
                    if (!string.IsNullOrWhiteSpace(childName))
                    {
                        var next = string.IsNullOrWhiteSpace(folder) ? childName : $"{folder}/{childName}";
                        await DiscoverFolderAsync(adaptor, next, tables, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            if (!json.RootElement.TryGetProperty("services", out var services)) { return; }
            foreach (var service in services.EnumerateArray())
            {
                var name = service.GetProperty("name").GetString();
                var type = service.GetProperty("type").GetString();
                if (string.IsNullOrWhiteSpace(name) || (type != "MapServer" && type != "FeatureServer")) { continue; }
                var serviceUrl = $"{BaseUrl}/{adaptor}/rest/services/{name}/{type}";
                using var serviceJson = await GetJsonAsync(serviceUrl, cancellationToken).ConfigureAwait(false);
                AddLayers(serviceJson.RootElement, serviceUrl, adaptor, name, type, tables);
            }
        }

        private static void AddLayers(JsonElement root, string serviceUrl, string adaptor, string serviceName, string serviceType, List<RawTablePoco> tables)
        {
            AddCollection("layers");
            AddCollection("tables");
            void AddCollection(string property)
            {
                if (!root.TryGetProperty(property, out var items)) { return; }
                foreach (var item in items.EnumerateArray())
                {
                if (item.TryGetProperty("subLayerIds", out var subLayerIds) && subLayerIds.ValueKind == JsonValueKind.Array)
                {
                    continue;
                }
                    var id = item.GetProperty("id").GetInt32();
                    var display = item.TryGetProperty("name", out var n) ? n.GetString() : id.ToString();
                    var layerUrl = $"{serviceUrl}/{id}";
                    tables.Add(new RawTablePoco
                    {
                        Name = EncodeTableName(layerUrl),
                        DisplayName = $"{adaptor} | {serviceName} | {display} ({serviceType}/{id})"
                    });
                }
            }
        }

        private async Task<ArcLayer> GetLayerAsync(string tableName, CancellationToken cancellationToken)
        {
            var url = DecodeTableName(tableName);
            using var json = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            var fields = new List<ArcField>();
            if (!root.TryGetProperty("fields", out var fieldArray))
            {
                throw new InvalidOperationException($"ArcGIS layer has no fields: {url}");
            }

            foreach (var field in fieldArray.EnumerateArray())
            {
                var name = field.GetProperty("name").GetString() ?? throw new InvalidOperationException("Field name missing.");
                var type = field.GetProperty("type").GetString() ?? "esriFieldTypeString";
                fields.Add(new ArcField(name, type, ReadCodedValues(field)));
            }

            string? subtypeField = null;
            if (root.TryGetProperty("typeIdField", out var typeIdField) && typeIdField.ValueKind == JsonValueKind.String)
            {
                subtypeField = typeIdField.GetString();
            }
            else if (root.TryGetProperty("subtypeField", out var subtypeFieldElement) && subtypeFieldElement.ValueKind == JsonValueKind.String)
            {
                subtypeField = subtypeFieldElement.GetString();
            }

            var subtypeDomains = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            var subtypeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadSubtypeMetadata(root, "types", "id", subtypeDomains, subtypeNames);
            ReadSubtypeMetadata(root, "subtypes", "code", subtypeDomains, subtypeNames);
            return new ArcLayer(fields, subtypeField, subtypeDomains, subtypeNames);
        }

        private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            var requestUri = new Uri(url + "?f=json&token=" + Uri.EscapeDataString(token));
            using var response = await _http.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            ThrowIfArcGisError(json.RootElement, url);
            return json;
        }

        private static void ThrowIfArcGisError(JsonElement root, string url)
        {
            if (root.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var code) && code.GetInt32() == 403)
                {
                    return;
                }

                throw new InvalidOperationException($"ArcGIS error from {url}: {error}");
            }
        }

        private static FormulaType GetFormulaType(string esriType) => esriType switch
        {
            "esriFieldTypeOID" or "esriFieldTypeInteger" or "esriFieldTypeSmallInteger" or "esriFieldTypeSingle" or "esriFieldTypeDouble" => FormulaType.Number,
            "esriFieldTypeDate" => FormulaType.DateTime,
            "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => FormulaType.Guid,
            _ => FormulaType.String
        };

        private static object? ConvertJsonValue(JsonElement value, string esriType)
        {
            if (value.ValueKind == JsonValueKind.Null) { return null; }
            if (esriType == "esriFieldTypeDate" && value.TryGetInt64(out var epoch))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime;
            }
            if ((esriType == "esriFieldTypeGUID" || esriType == "esriFieldTypeGlobalID") && Guid.TryParse(value.ToString().Trim('{', '}'), out var guid))
            {
                return guid;
            }
            if (GetFormulaType(esriType) == FormulaType.Number && value.TryGetDouble(out var number)) { return number; }
            return value.ToString();
        }

        private static string EncodeTableName(string url) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string DecodeTableName(string name)
        {
            var value = name.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var url = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
            if (!url.StartsWith(BaseUrl + "/", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Invalid PROD ArcGIS table identifier."); }
            return url;
        }
        private static void ValidateDataset(string dataset)
        {
            if (!string.Equals(dataset, DatasetName, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Only the PROD dataset is supported."); }
        }
        private static object? DecodeDomainValue(ArcLayer layer, ArcField field, IReadOnlyDictionary<string, object?> row, object? value)
        {
            if (value == null)
            {
                return null;
            }

            IReadOnlyDictionary<string, string>? codedValues = null;
            if (!string.IsNullOrWhiteSpace(layer.SubtypeField) && row.TryGetValue(layer.SubtypeField, out var subtypeValue) && subtypeValue != null)
            {
                var subtypeKey = NormalizeCode(subtypeValue);
                if (string.Equals(field.Name, layer.SubtypeField, StringComparison.OrdinalIgnoreCase) && layer.SubtypeNames.TryGetValue(subtypeKey, out var subtypeName))
                {
                    return subtypeName;
                }

                if (layer.SubtypeDomains.TryGetValue(subtypeKey, out var fieldDomains) && fieldDomains.TryGetValue(field.Name, out var subtypeDomain))
                {
                    codedValues = subtypeDomain;
                }
            }

            codedValues ??= field.CodedValues;
            if (codedValues.Count == 0)
            {
                return value;
            }

            var code = NormalizeCode(value);
            return codedValues.TryGetValue(code, out var decoded) ? decoded : code;
        }

        private static IReadOnlyDictionary<string, string> ReadCodedValues(JsonElement owner)
        {
            if (!owner.TryGetProperty("domain", out var domain))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return ReadCodedValuesFromDomain(domain);
        }

        private static IReadOnlyDictionary<string, string> ReadCodedValuesFromDomain(JsonElement domain)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (domain.ValueKind != JsonValueKind.Object || !domain.TryGetProperty("codedValues", out var codedValues) || codedValues.ValueKind != JsonValueKind.Array)
            {
                return values;
            }

            foreach (var codedValue in codedValues.EnumerateArray())
            {
                if (!codedValue.TryGetProperty("code", out var codeElement) || !codedValue.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    values[NormalizeCode(codeElement)] = name;
                }
            }

            return values;
        }

        private static void ReadSubtypeMetadata(
            JsonElement root,
            string collectionName,
            string codePropertyName,
            IDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> target,
            IDictionary<string, string> subtypeNames)
        {
            if (!root.TryGetProperty(collectionName, out var subtypes) || subtypes.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var subtype in subtypes.EnumerateArray())
            {
                if (!subtype.TryGetProperty(codePropertyName, out var codeElement) || !subtype.TryGetProperty("domains", out var domains) || domains.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var fieldDomains = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var domainProperty in domains.EnumerateObject())
                {
                    var codedValues = ReadCodedValuesFromDomain(domainProperty.Value);
                    if (codedValues.Count > 0)
                    {
                        fieldDomains[domainProperty.Name] = codedValues;
                    }
                }

                var subtypeKey = NormalizeCode(codeElement);
                target[subtypeKey] = fieldDomains;
                if (subtype.TryGetProperty("name", out var nameElement))
                {
                    var subtypeName = nameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(subtypeName))
                    {
                        subtypeNames[subtypeKey] = subtypeName;
                    }
                }
            }
        }

        private static string NormalizeCode(JsonElement value)
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => value.ToString(),
            };
        }

        private static string NormalizeCode(object value)
        {
            return value switch
            {
                double number when number == Math.Truncate(number) => number.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
        }

        private sealed record ArcField(string Name, string Type, IReadOnlyDictionary<string, string> CodedValues);
        private sealed record ArcLayer(
            IReadOnlyList<ArcField> Fields,
            string? SubtypeField,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> SubtypeDomains,
            IReadOnlyDictionary<string, string> SubtypeNames);
    }
}
