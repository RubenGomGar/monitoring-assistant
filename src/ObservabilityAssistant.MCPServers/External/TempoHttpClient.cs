using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.MCPServers.Base;

namespace ObservabilityAssistant.MCPServers.External;

/// <summary>
/// Cliente HTTP para consultar Tempo a través del proxy de Grafana
/// </summary>
public class TempoHttpClient : MCPServer
{
    private readonly HttpClient _httpClient;
    private readonly string _grafanaUrl;
    private readonly string _grafanaApiKey;
    private readonly string _tempoDataSourceUid;

    public TempoHttpClient(
        string grafanaUrl,
        string grafanaApiKey,
        string tempoDataSourceUid,
        ILogger<TempoHttpClient> logger) 
        : base(logger)
    {
        _grafanaUrl = grafanaUrl.TrimEnd('/');
        _grafanaApiKey = grafanaApiKey;
        _tempoDataSourceUid = tempoDataSourceUid;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_grafanaUrl)
        };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_grafanaApiKey}");
    }

    public override async Task InitializeAsync()
    {
        Logger.LogInformation("Initializing Tempo HTTP Client via Grafana proxy: {Url}", _grafanaUrl);
        
        RegisterTools();
        
        Logger.LogInformation("Registered {ToolCount} Tempo tools", Tools.Count);
    }

    private void RegisterTools()
    {
        RegisterTool(
            "search_tempo_traces",
            "Searches for traces in Tempo using TraceQL query. Returns a summary of matching traces with their services, spans, and durations.",
            async (args) =>
            {
                var query = args.GetValueOrDefault("query")?.ToString();

                // Allow providing service/serviceName instead of a full TraceQL query
                if (string.IsNullOrWhiteSpace(query))
                {
                    var service = args.GetValueOrDefault("service")?.ToString()
                                 ?? args.GetValueOrDefault("serviceName")?.ToString();
                    if (!string.IsNullOrWhiteSpace(service))
                    {
                        // TraceQL: filter traces by service name
                        query = $"{{resource.service.name=\"{service}\"}}";
                    }
                }

                if (string.IsNullOrWhiteSpace(query))
                {
                    return MCPResponse.Fail("Provide either 'query' (TraceQL) or 'service'/'serviceName'. Example query: {resource.service.name=\"demo-api\"}");
                }

                // Robust limit parsing from different JSON argument shapes
                int limit = 20;
                if (args.TryGetValue("limit", out var limitRaw) && limitRaw is not null)
                {
                    try
                    {
                        if (limitRaw is JsonElement je)
                        {
                            if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var l1))
                                limit = l1;
                            else if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var l2))
                                limit = l2;
                        }
                        else if (limitRaw is IConvertible)
                        {
                            limit = Convert.ToInt32(limitRaw);
                        }
                        else if (int.TryParse(limitRaw.ToString(), out var l3))
                        {
                            limit = l3;
                        }
                    }
                    catch
                    {
                        // keep default limit
                    }
                }

                try
                {
                    // Usar el API de datasource proxy de Grafana para consultar Tempo
                    // El endpoint correcto es /api/datasources/proxy/uid/{uid}/api/search
                    var searchUrl = $"/api/datasources/proxy/uid/{_tempoDataSourceUid}/api/search?q={Uri.EscapeDataString(query)}&limit={limit}";
                    
                    Logger.LogInformation("Searching Tempo traces via Grafana proxy with query: {Query}", query);
                    Logger.LogInformation("Using URL: {Url}", searchUrl);
                    
                    var response = await _httpClient.GetAsync(searchUrl);
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        Logger.LogError("Grafana proxy error ({StatusCode}): {Error}", response.StatusCode, errorContent);
                        return MCPResponse.Fail($"Grafana proxy error ({response.StatusCode}): {errorContent}");
                    }
                    
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    
                    if (result == null)
                    {
                        return MCPResponse.Fail("No response from Tempo via Grafana proxy");
                    }

                    // Procesar los resultados
                    if (!result.RootElement.TryGetProperty("traces", out var traces))
                    {
                        return MCPResponse.Ok("No traces found matching the query");
                    }

                    var traceList = new List<object>();
                    foreach (var trace in traces.EnumerateArray())
                    {
                        var traceId = trace.TryGetProperty("traceID", out var tid) ? tid.GetString() : "unknown";
                        var rootServiceName = trace.TryGetProperty("rootServiceName", out var rsn) ? rsn.GetString() : "unknown";
                        var rootTraceName = trace.TryGetProperty("rootTraceName", out var rtn) ? rtn.GetString() : "unknown";
                        var startTimeUnixNano = trace.TryGetProperty("startTimeUnixNano", out var st) ? st.GetString() : "0";
                        var durationMs = trace.TryGetProperty("durationMs", out var dm) ? dm.GetInt32() : 0;
                        var spanSets = trace.TryGetProperty("spanSets", out var ss) ? ss.GetArrayLength() : 0;

                        var startTime = "unknown";
                        if (startTimeUnixNano != "0" && !string.IsNullOrEmpty(startTimeUnixNano))
                        {
                            try
                            {
                                startTime = DateTimeOffset.FromUnixTimeMilliseconds(
                                    long.Parse(startTimeUnixNano) / 1_000_000
                                ).ToString("yyyy-MM-dd HH:mm:ss");
                            }
                            catch { }
                        }

                        traceList.Add(new
                        {
                            traceId,
                            rootServiceName,
                            rootTraceName,
                            durationMs,
                            spanSets,
                            startTime
                        });
                    }

                    var summary = new
                    {
                        query,
                        totalTraces = traceList.Count,
                        traces = traceList
                    };

                    return MCPResponse.Ok(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (HttpRequestException ex)
                {
                    Logger.LogError(ex, "Failed to search Tempo traces via Grafana proxy");
                    return MCPResponse.Fail($"Error querying Tempo via Grafana: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Unexpected error searching Tempo traces");
                    return MCPResponse.Fail($"Unexpected error: {ex.Message}");
                }
            });
    }
}
