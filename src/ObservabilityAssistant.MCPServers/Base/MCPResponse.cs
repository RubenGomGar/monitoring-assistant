using System.Text.Json;
using System.Text.Json.Serialization;
namespace ObservabilityAssistant.MCPServers.Base;
public class MCPResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    [JsonPropertyName("data")]
    public object? Data { get; set; }
    [JsonPropertyName("error")]
    public string? Error { get; set; }
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
    public static MCPResponse Ok(object? data = null, Dictionary<string, object>? metadata = null)
    {
        return new MCPResponse
        {
            Success = true,
            Data = data,
            Metadata = metadata
        };
    }
    public static MCPResponse Fail(string error, object? data = null)
    {
        return new MCPResponse
        {
            Success = false,
            Error = error,
            Data = data
        };
    }
}
