using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.MCPServers.Base;
namespace ObservabilityAssistant.MCPServers.Tempo;
public class TempoMCPServer : MCPServer
{
    private readonly HttpClient _httpClient;
    private readonly string _tempoUrl;
    public TempoMCPServer(
        string tempoUrl,
        ILogger<TempoMCPServer> logger) 
        : base(logger)
    {
        _tempoUrl = tempoUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_tempoUrl)
        };
    }
    public override async Task InitializeAsync()
    {
        Logger.LogInformation("Initializing Tempo MCP Server with URL: {Url}", _tempoUrl);
        try
        {
            var response = await _httpClient.GetAsync("/api/search/tags");
            response.EnsureSuccessStatusCode();
            Logger.LogInformation("Successfully connected to Tempo at {Url}", _tempoUrl);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to connect to Tempo at {Url}", _tempoUrl);
            throw;
        }
        RegisterTools();
        Logger.LogInformation("Registered {ToolCount} Tempo tools", Tools.Count);
    }
    private void RegisterTools()
    {
        RegisterTool(
            "get_trace",
            "Retrieves a specific trace by its trace ID from Tempo.",
            async (args) =>
            {
                var traceId = args.GetValueOrDefault("trace_id")?.ToString();
                if (string.IsNullOrEmpty(traceId))
                {
                    return MCPResponse.Fail("trace_id parameter is required");
                }
                try
                {
                    var response = await _httpClient.GetAsync($"/api/traces/{traceId}");
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var trace = result?.RootElement;
                    if (!trace.HasValue || trace.Value.ValueKind == JsonValueKind.Null)
                    {
                        return MCPResponse.Fail($"Trace not found: {traceId}");
                    }
                    var batches = trace?.GetProperty("batches").EnumerateArray().SelectMany(batch =>
                    {
                        var resource = batch.GetProperty("resource");
                        var serviceName = resource.GetProperty("attributes")
                            .EnumerateArray()
                            .FirstOrDefault(attr => attr.GetProperty("key").GetString() == "service.name")
                            .GetProperty("value")
                            .GetProperty("stringValue")
                            .GetString() ?? "unknown";
                        return batch.GetProperty("scopeSpans").EnumerateArray().SelectMany(scope =>
                        {
                            return scope.GetProperty("spans").EnumerateArray().Select(span =>
                            {
                                var spanId = Convert.ToHexString(
                                    Convert.FromBase64String(span.GetProperty("spanId").GetString()!));
                                var parentSpanId = span.TryGetProperty("parentSpanId", out var parent) 
                                    ? Convert.ToHexString(Convert.FromBase64String(parent.GetString()!))
                                    : null;
                                return new
                                {
                                    serviceName,
                                    spanId,
                                    parentSpanId,
                                    name = span.GetProperty("name").GetString(),
                                    kind = span.GetProperty("kind").GetInt32(),
                                    startTimeUnixNano = span.GetProperty("startTimeUnixNano").GetString(),
                                    endTimeUnixNano = span.GetProperty("endTimeUnixNano").GetString(),
                                    durationNano = long.Parse(span.GetProperty("endTimeUnixNano").GetString()!) - 
                                                  long.Parse(span.GetProperty("startTimeUnixNano").GetString()!),
                                    attributes = span.GetProperty("attributes").EnumerateArray()
                                        .ToDictionary(
                                            attr => attr.GetProperty("key").GetString()!,
                                            attr => attr.GetProperty("value").EnumerateObject().First().Value.ToString()
                                        )
                                };
                            });
                        });
                    }).ToList();
                    var totalDurationMs = batches.Any() 
                        ? batches.Max(s => s.durationNano) / 1_000_000.0
                        : 0;
                    return MCPResponse.Ok(new
                    {
                        traceId,
                        spanCount = batches.Count,
                        services = batches.Select(s => s.serviceName).Distinct().ToList(),
                        totalDurationMs,
                        spans = batches
                    });
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return MCPResponse.Fail($"Trace not found: {traceId}");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get trace: {TraceId}", traceId);
                    return MCPResponse.Fail($"Failed to get trace: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "trace_id", Type = "string", Required = true, Description = "Trace ID to retrieve (hexadecimal format)" }
            }
        );
        RegisterTool(
            "search_traces",
            "Searches for traces in Tempo based on tags and time range.",
            async (args) =>
            {
                var serviceName = args.GetValueOrDefault("service_name")?.ToString();
                var operationName = args.GetValueOrDefault("operation_name")?.ToString();
                var minDuration = args.GetValueOrDefault("min_duration")?.ToString();
                var maxDuration = args.GetValueOrDefault("max_duration")?.ToString();
                var limit = args.GetValueOrDefault("limit") != null 
                    ? Convert.ToInt32(args["limit"]) 
                    : 20;
                try
                {
                    var queryParams = new List<string> { $"limit={limit}" };
                    var tags = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(serviceName))
                        tags["service.name"] = serviceName;
                    if (!string.IsNullOrEmpty(operationName))
                        tags["name"] = operationName;
                    if (tags.Any())
                        queryParams.Add($"tags={Uri.EscapeDataString(JsonSerializer.Serialize(tags))}");
                    if (!string.IsNullOrEmpty(minDuration))
                        queryParams.Add($"minDuration={minDuration}");
                    if (!string.IsNullOrEmpty(maxDuration))
                        queryParams.Add($"maxDuration={maxDuration}");
                    var queryString = $"/api/search?{string.Join("&", queryParams)}";
                    var response = await _httpClient.GetAsync(queryString);
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var traces = result?.RootElement.GetProperty("traces").EnumerateArray().Select(trace =>
                    {
                        return new
                        {
                            traceId = trace.GetProperty("traceID").GetString(),
                            rootServiceName = trace.GetProperty("rootServiceName").GetString(),
                            rootTraceName = trace.GetProperty("rootTraceName").GetString(),
                            startTimeUnixNano = trace.GetProperty("startTimeUnixNano").GetString(),
                            durationMs = trace.GetProperty("durationMs").GetInt32(),
                            spanSets = trace.GetProperty("spanSets").EnumerateArray().Select(ss => new
                            {
                                spanCount = ss.GetProperty("spans").GetArrayLength(),
                                matched = ss.GetProperty("matched").GetInt32()
                            }).ToList()
                        };
                    }).ToList();
                    return MCPResponse.Ok(new
                    {
                        traceCount = traces?.Count ?? 0,
                        traces
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to search traces");
                    return MCPResponse.Fail($"Failed to search traces: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "service_name", Type = "string", Required = false, Description = "Filter by service name" },
                new() { Name = "operation_name", Type = "string", Required = false, Description = "Filter by operation/span name" },
                new() { Name = "min_duration", Type = "string", Required = false, Description = "Minimum duration (e.g., '100ms', '1s')" },
                new() { Name = "max_duration", Type = "string", Required = false, Description = "Maximum duration (e.g., '5s')" },
                new() { Name = "limit", Type = "number", Required = false, Description = "Maximum number of traces to return. Defaults to 20" }
            }
        );
        RegisterTool(
            "get_services",
            "Retrieves the list of services that have sent traces to Tempo.",
            async (args) =>
            {
                try
                {
                    var response = await _httpClient.GetAsync("/api/search/tags/service.name/values");
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var services = result?.RootElement
                        .GetProperty("tagValues")
                        .EnumerateArray()
                        .Select(v => v.GetProperty("value").GetString())
                        .ToList();
                    return MCPResponse.Ok(new
                    {
                        serviceCount = services?.Count ?? 0,
                        services
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get services");
                    return MCPResponse.Fail($"Failed to get services: {ex.Message}");
                }
            }
        );
    }
}
