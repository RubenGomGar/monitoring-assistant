using System.Text.Json.Serialization;
namespace ObservabilityAssistant.Core.Models;
public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
    public static ChatMessage System(string content) => new() { Role = "system", Content = content };
    public static ChatMessage User(string content) => new() { Role = "user", Content = content };
    public static ChatMessage Assistant(string content) => new() { Role = "assistant", Content = content };
    public static ChatMessage Tool(string content, string toolCallId) => new() 
    { 
        Role = "tool", 
        Content = content, 
        ToolCallId = toolCallId 
    };
}
public class ToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";
    [JsonPropertyName("function")]
    public FunctionCall Function { get; set; } = new();
}
public class FunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
public class ChatResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public bool RequiresToolExecution => ToolCalls?.Any() == true;
}
