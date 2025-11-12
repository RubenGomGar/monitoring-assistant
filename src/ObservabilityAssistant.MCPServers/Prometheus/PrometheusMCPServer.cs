using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.MCPServers.Base;
namespace ObservabilityAssistant.MCPServers.Prometheus;
public class PrometheusMCPServer : MCPServer
{
    private readonly HttpClient _httpClient;
    private readonly string _prometheusUrl;
    public PrometheusMCPServer(
        string prometheusUrl,
        ILogger<PrometheusMCPServer> logger) 
        : base(logger)
    {
        _prometheusUrl = prometheusUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_prometheusUrl)
        };
    }
    public override async Task InitializeAsync()
    {
        Logger.LogInformation("Initializing Prometheus MCP Server with URL: {Url}", _prometheusUrl);
        try
        {
            var response = await _httpClient.GetAsync("/api/v1/query?query=prometheus_build_info");
            response.EnsureSuccessStatusCode();
            Logger.LogInformation("Successfully connected to Prometheus at {Url}", _prometheusUrl);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to connect to Prometheus at {Url}", _prometheusUrl);
            throw;
        }
        RegisterTools();
        Logger.LogInformation("Registered {ToolCount} Prometheus tools", Tools.Count);
    }
    private void RegisterTools()
    {
        RegisterTool(
            "query_metrics",
            "Executes a PromQL query to retrieve metrics from Prometheus. Use this to get current values or instant queries.",
            async (args) =>
            {
                var query = args.GetValueOrDefault("query")?.ToString();
                if (string.IsNullOrEmpty(query))
                {
                    return MCPResponse.Fail("query parameter is required");
                }
                try
                {
                    var response = await _httpClient.GetAsync($"/api/v1/query?query={Uri.EscapeDataString(query)}");
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var data = result?.RootElement.GetProperty("data");
                    if (data?.GetProperty("result").GetArrayLength() == 0)
                    {
                        return MCPResponse.Ok(new
                        {
                            query,
                            message = "No data found for this query",
                            results = Array.Empty<object>()
                        });
                    }
                    var results = data?.GetProperty("result").EnumerateArray().Select(item =>
                    {
                        var metric = item.GetProperty("metric");
                        var value = item.GetProperty("value");
                        return new
                        {
                            metric = JsonSerializer.Deserialize<Dictionary<string, string>>(metric.GetRawText()),
                            timestamp = value[0].GetDouble(),
                            value = value[1].GetString()
                        };
                    }).ToList();
                    return MCPResponse.Ok(new
                    {
                        query,
                        resultCount = results?.Count ?? 0,
                        results
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
                new() { Name = "query", Type = "string", Required = true, Description = "PromQL query to execute (e.g., 'up', 'rate(http_requests_total[5m])')" }
            }
        );
        RegisterTool(
            "query_range",
            "Executes a PromQL range query to retrieve metrics over a time range from Prometheus.",
            async (args) =>
            {
                var query = args.GetValueOrDefault("query")?.ToString();
                var start = args.GetValueOrDefault("start")?.ToString() ?? DateTime.UtcNow.AddHours(-1).ToString("O");
                var end = args.GetValueOrDefault("end")?.ToString() ?? DateTime.UtcNow.ToString("O");
                var step = args.GetValueOrDefault("step")?.ToString() ?? "15s";
                if (string.IsNullOrEmpty(query))
                {
                    return MCPResponse.Fail("query parameter is required");
                }
                try
                {
                    var queryString = $"/api/v1/query_range?query={Uri.EscapeDataString(query)}&start={Uri.EscapeDataString(start)}&end={Uri.EscapeDataString(end)}&step={step}";
                    var response = await _httpClient.GetAsync(queryString);
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var data = result?.RootElement.GetProperty("data");
                    if (data?.GetProperty("result").GetArrayLength() == 0)
                    {
                        return MCPResponse.Ok(new
                        {
                            query,
                            start,
                            end,
                            step,
                            message = "No data found for this query",
                            results = Array.Empty<object>()
                        });
                    }
                    var results = data?.GetProperty("result").EnumerateArray().Select(item =>
                    {
                        var metric = item.GetProperty("metric");
                        var values = item.GetProperty("values").EnumerateArray().Select(v =>
                        {
                            return new
                            {
                                timestamp = DateTimeOffset.FromUnixTimeSeconds((long)v[0].GetDouble()).ToString("O"),
                                value = v[1].GetString()
                            };
                        }).ToList();
                        return new
                        {
                            metric = JsonSerializer.Deserialize<Dictionary<string, string>>(metric.GetRawText()),
                            values
                        };
                    }).ToList();
                    return MCPResponse.Ok(new
                    {
                        query,
                        start,
                        end,
                        step,
                        seriesCount = results?.Count ?? 0,
                        results
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to execute range query: {Query}", query);
                    return MCPResponse.Fail($"Failed to execute range query: {ex.Message}");
                }
            },
            new List<MCPToolParameter>
            {
                new() { Name = "query", Type = "string", Required = true, Description = "PromQL query to execute" },
                new() { Name = "start", Type = "string", Required = false, Description = "Start time (RFC3339 or Unix timestamp). Defaults to 1 hour ago" },
                new() { Name = "end", Type = "string", Required = false, Description = "End time (RFC3339 or Unix timestamp). Defaults to now" },
                new() { Name = "step", Type = "string", Required = false, Description = "Query resolution step width (e.g., '15s', '1m'). Defaults to 15s" }
            }
        );
        RegisterTool(
            "get_targets",
            "Retrieves the list of active targets that Prometheus is scraping metrics from.",
            async (args) =>
            {
                try
                {
                    var response = await _httpClient.GetAsync("/api/v1/targets");
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var activeTargets = result?.RootElement
                        .GetProperty("data")
                        .GetProperty("activeTargets")
                        .EnumerateArray()
                        .Select(target =>
                        {
                            return new
                            {
                                scrapePool = target.GetProperty("scrapePool").GetString(),
                                scrapeUrl = target.GetProperty("scrapeUrl").GetString(),
                                health = target.GetProperty("health").GetString(),
                                lastScrape = target.GetProperty("lastScrape").GetString(),
                                lastScrapeDuration = target.GetProperty("lastScrapeDuration").GetDouble(),
                                labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                                    target.GetProperty("labels").GetRawText())
                            };
                        }).ToList();
                    var healthyCount = activeTargets?.Count(t => t.health == "up") ?? 0;
                    var unhealthyCount = activeTargets?.Count(t => t.health == "down") ?? 0;
                    return MCPResponse.Ok(new
                    {
                        totalTargets = activeTargets?.Count ?? 0,
                        healthyTargets = healthyCount,
                        unhealthyTargets = unhealthyCount,
                        targets = activeTargets
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get targets");
                    return MCPResponse.Fail($"Failed to get targets: {ex.Message}");
                }
            }
        );
        RegisterTool(
            "get_alerts",
            "Retrieves currently active alerts from Prometheus Alertmanager.",
            async (args) =>
            {
                try
                {
                    var response = await _httpClient.GetAsync("/api/v1/alerts");
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonDocument>();
                    var alerts = result?.RootElement
                        .GetProperty("data")
                        .GetProperty("alerts")
                        .EnumerateArray()
                        .Select(alert =>
                        {
                            return new
                            {
                                name = alert.GetProperty("labels").GetProperty("alertname").GetString(),
                                state = alert.GetProperty("state").GetString(),
                                activeAt = alert.TryGetProperty("activeAt", out var activeAt) ? activeAt.GetString() : null,
                                value = alert.GetProperty("value").GetString(),
                                labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                                    alert.GetProperty("labels").GetRawText()),
                                annotations = JsonSerializer.Deserialize<Dictionary<string, string>>(
                                    alert.GetProperty("annotations").GetRawText())
                            };
                        }).ToList();
                    var firingCount = alerts?.Count(a => a.state == "firing") ?? 0;
                    var pendingCount = alerts?.Count(a => a.state == "pending") ?? 0;
                    return MCPResponse.Ok(new
                    {
                        totalAlerts = alerts?.Count ?? 0,
                        firingAlerts = firingCount,
                        pendingAlerts = pendingCount,
                        alerts
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to get alerts");
                    return MCPResponse.Fail($"Failed to get alerts: {ex.Message}");
                }
            }
        );
    }
}
