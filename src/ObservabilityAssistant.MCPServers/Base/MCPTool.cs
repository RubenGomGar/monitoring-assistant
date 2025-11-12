using System.Text.Json;
using System.Text.Json.Serialization;
namespace ObservabilityAssistant.MCPServers.Base;
public class MCPToolParameter
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";
    [JsonPropertyName("description")]
    public string? Description { get; set; }
    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;
    [JsonPropertyName("enum")]
    public List<string>? EnumValues { get; set; }
}
public class MCPTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
    [JsonPropertyName("parameters")]
    public List<MCPToolParameter> Parameters { get; set; } = new();
    [JsonIgnore]
    public JsonElement? RawInputSchema { get; set; }
    [JsonIgnore]
    public Func<Dictionary<string, object?>, Task<MCPResponse>> Handler { get; set; } = _ => 
        Task.FromResult(new MCPResponse { Success = false, Error = "Handler not implemented" });
}
