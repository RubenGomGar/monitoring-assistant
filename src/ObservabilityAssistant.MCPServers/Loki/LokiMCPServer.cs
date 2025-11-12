using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.MCPServers.Base;
namespace ObservabilityAssistant.MCPServers.Loki;
public class LokiMCPServer : MCPServer
{
    private readonly HttpClient _httpClient;
    private readonly string _lokiUrl;
    public LokiMCPServer(
        string lokiUrl,
        ILogger<LokiMCPServer> logger) 
        : base(logger)
    {
        _lokiUrl = lokiUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_lokiUrl)
        };
    }
    public override async Task InitializeAsync()
    {
        Logger.LogInformation("Initializing Loki MCP Server with URL: {Url}", _lokiUrl);
        try
        {
            var response = await _httpClient.GetAsync("/loki/api/v1/labels");
            response.EnsureSuccessStatusCode();
            Logger.LogInformation("Successfully connected to Loki at {Url}", _lokiUrl);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to connect to Loki at {Url}", _lokiUrl);
            throw;
        }
        RegisterTools();
        Logger.LogInformation("Registered {ToolCount} Loki tools", Tools.Count);
    }
    private void RegisterTools()
    {
        RegisterTool(
            "query_logs",
            "Executes a LogQL query to retrieve logs from Loki. Use label matchers to filter logs (e.g., {job=\"demo-api\"})",
            async (args) =>
            {
                var query = args.GetValueOrDefault("query")?.ToString();
                int limit = 100;
                if (args.GetValueOrDefault("limit") is { } limitValue)
                {
                    if (limitValue is System.Text.Json.JsonElement jsonElement)
                        limit = jsonElement.GetInt32();
                    else
                        limit = Convert.ToInt32(limitValue);
                }
                var start = args.GetValueOrDefault("start")?.ToString();
                var end = args.GetValueOrDefault("end")?.ToString();
                if (string.IsNullOrEmpty(query))
                {
                    return MCPResponse.Fail("query parameter is required");
                }
                try
                {
                    var queryParams = new List<string>
                    {
                        $"query={Uri.EscapeDataString(query)}",
                        $"limit={limit}"
                    };
                    if (!string.IsNullOrEmpty(start))
                        queryParams.Add($"start={start}");
                    if (!string.IsNullOrEmpty(end))
                        queryParams.Add($"end={end}");
                    var queryString = $"/loki/api/v1/query_range?{string.Join("&", queryParams)}";
                    var response = await _httpClient.GetAsync(queryString);
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var data = result?.RootElement.GetProperty("data");
                    var resultType = data?.GetProperty("resultType").GetString();
                    if (data?.GetProperty("result").GetArrayLength() == 0)
                    {
                        return MCPResponse.Ok(new
                        {
                            query,
                            message = "No logs found for this query",
                            logs = Array.Empty<object>()
                        });
                    }
                    var logs = data?.GetProperty("result").EnumerateArray().SelectMany(stream =>
                    {
                        var labelsJson = stream.GetProperty("stream").GetRawText();
                        var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(labelsJson) 
                            ?? new Dictionary<string, string>();
                        return stream.GetProperty("values").EnumerateArray().Select(entry =>
                        {
                            var timestamp = entry[0].GetString();
                            var line = entry[1].GetString();
                            return new
                            {
                                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                                    long.Parse(timestamp!) / 1000000).ToString("O"),
                                line = line ?? string.Empty,
                                labels = new Dictionary<string, string>(labels) // Create new instance
                            };
                        });
                    }).ToList();
                    return MCPResponse.Ok(new
                    {
                        query,
                        logCount = logs?.Count ?? 0,
                        logs
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to execute query: {Query}", query);
                    return MCPResponse.Fail($"Failed to execute query: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "query", Type = "string", Required = true, Description = "LogQL query (e.g., '{job=\"demo-api\"}', '{namespace=\"apps\"} |= \"error\"')" },
                new() { Name = "limit", Type = "number", Required = false, Description = "Maximum number of log entries to return. Defaults to 100" },
                new() { Name = "start", Type = "string", Required = false, Description = "Start time as Unix nanoseconds timestamp or RFC3339. Defaults to 1 hour ago" },
                new() { Name = "end", Type = "string", Required = false, Description = "End time as Unix nanoseconds timestamp or RFC3339. Defaults to now" }
            }
        );
        RegisterTool(
            "get_labels",
            "Retrieves all available label names from Loki. Useful for discovering what labels you can filter by.",
            async (args) =>
            {
                try
                {
                    var response = await _httpClient.GetAsync("/loki/api/v1/labels");
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var labels = result?.RootElement
                        .GetProperty("data")
                        .EnumerateArray()
                        .Select(l => l.GetString())
                        .ToList();
                    return MCPResponse.Ok(new
                    {
                        totalLabels = labels?.Count ?? 0,
                        labels
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get labels");
                    return MCPResponse.Fail($"Failed to get labels: {ex.Message}");
                }
            }
        );
        RegisterTool(
            "get_label_values",
            "Retrieves all values for a specific label name. Use this to discover available jobs, namespaces, etc.",
            async (args) =>
            {
                var label = args.GetValueOrDefault("label")?.ToString();
                if (string.IsNullOrEmpty(label))
                {
                    return MCPResponse.Fail("label parameter is required");
                }
                try
                {
                    var response = await _httpClient.GetAsync($"/loki/api/v1/label/{label}/values");
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var values = result?.RootElement
                        .GetProperty("data")
                        .EnumerateArray()
                        .Select(v => v.GetString())
                        .ToList();
                    return MCPResponse.Ok(new
                    {
                        label,
                        totalValues = values?.Count ?? 0,
                        values
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get label values for: {Label}", label);
                    return MCPResponse.Fail($"Failed to get label values: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "label", Type = "string", Required = true, Description = "Label name to get values for (e.g., 'job', 'namespace', 'pod')" }
            }
        );
    }
}
