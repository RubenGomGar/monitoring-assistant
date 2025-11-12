using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.Core.Models;
using ObservabilityAssistant.MCPServers.Base;
using OpenAI.Chat;
using ChatMessage = ObservabilityAssistant.Core.Models.ChatMessage;
namespace ObservabilityAssistant.Core.AI;
public class AIOrchestrator
{
    private readonly OpenAIService _openAIClient;
    private readonly List<MCPServer> _mcpServers;
    private readonly ILogger<AIOrchestrator> _logger;
    private readonly List<ChatMessage> _conversationHistory;
    private const string SystemPrompt = @"You are an AI assistant specialized in Kubernetes and observability systems.
You have access to tools from two main sources: Kubernetes and Grafana.
TOOL SELECTION RULES:
KUBERNETES TOOLS (k8s_* prefix):
Use for infrastructure queries:
- Listing pods, deployments, services, namespaces, workloads
- Getting pod/container status and health
- Resource information (what is running, what exists)
- Kubernetes object details
Examples: 'list pods', 'show deployments', 'what workloads are in namespace X'
GRAFANA TOOLS (grafana_* prefix):  
Use for observability queries:
- LOGS: Always use Grafana/Loki for log queries (never use k8s logs)
- Metrics: Time-series data, CPU, memory, network stats
- Dashboards: Creating or viewing visualization
- Traces: Distributed tracing via Tempo
Examples: 'show logs', 'error logs from pod X', 'CPU metrics', 'create dashboard'
CRITICAL RULES:
1. For ANY log query → Use grafana_* tools (Loki)
2. For 'what is running' → Use k8s_* tools
3. For metrics over time → Use grafana_* tools (Prometheus)
4. For current status → Use k8s_* tools
5. For dashboards/visualization → Use grafana_* tools
Guidelines:
- Be concise and clear in your responses
- When showing data, format it nicely for readability
- If you encounter errors, explain them in a user-friendly way
- Suggest relevant follow-up actions when appropriate
- Always validate tool results before presenting them to the user
- If asked about multiple resources, break down the information clearly
- Explain which tool you're using and why
Available tools will be provided dynamically based on configured MCP servers.";
    public AIOrchestrator(
        OpenAIService openAIClient,
        IEnumerable<MCPServer> mcpServers,
        ILogger<AIOrchestrator> logger)
    {
        _openAIClient = openAIClient;
        _mcpServers = mcpServers.ToList();
        _logger = logger;
        _conversationHistory = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt)
        };
        _logger.LogInformation("AI Orchestrator initialized with {ServerCount} MCP servers", _mcpServers.Count);
    }
    public async Task<string> ProcessUserMessageAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Processing user message: {Message}", userMessage);
            _conversationHistory.Add(ChatMessage.User(userMessage));
            var tools = ConvertMCPToolsToOpenAIFunctions();
            var maxIterations = 10;
            var iteration = 0;
            while (iteration < maxIterations)
            {
                iteration++;
                _logger.LogDebug("Conversation iteration {Iteration}", iteration);
                var response = await _openAIClient.SendMessageAsync(
                    _conversationHistory,
                    tools,
                    cancellationToken);
                _logger.LogInformation("OpenAI response - Success: {Success}, HasMessage: {HasMessage}, RequiresTools: {RequiresTools}", 
                    response.Success, !string.IsNullOrEmpty(response.Message), response.RequiresToolExecution);
                if (!response.Success)
                {
                    return $"❌ Error: {response.Error}";
                }
                if (!string.IsNullOrEmpty(response.Message))
                {
                    _conversationHistory.Add(ChatMessage.Assistant(response.Message));
                    return response.Message;
                }
                if (response.RequiresToolExecution)
                {
                    _logger.LogInformation("Executing {ToolCount} tool calls", response.ToolCalls?.Count ?? 0);
                    await ExecuteToolCallsAsync(response.ToolCalls!, cancellationToken);
                    continue;
                }
                _logger.LogWarning("Received response with no message and no tool calls");
                return "I encountered an issue processing your request. Please try again.";
            }
            return "⚠️ Maximum conversation iterations reached. Please start a new conversation.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing user message");
            return $"❌ An error occurred: {ex.Message}";
        }
    }
    private async Task ExecuteToolCallsAsync(List<ToolCall> toolCalls, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing {ToolCallCount} tool calls", toolCalls.Count);
        _conversationHistory.Add(new ChatMessage
        {
            Role = "assistant",
            Content = string.Empty,
            ToolCalls = toolCalls
        });
        foreach (var toolCall in toolCalls)
        {
            try
            {
                var functionName = toolCall.Function.Name;
                var argumentsJson = toolCall.Function.Arguments;
                _logger.LogInformation("🔧 Using tool: {ToolName}", functionName);
                var result = await ExecuteMCPToolAsync(functionName, argumentsJson, cancellationToken);
                _conversationHistory.Add(ChatMessage.Tool(result, toolCall.Id));
                _logger.LogDebug("Tool {ToolName} executed, result length: {Length}", functionName, result.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error executing tool: {ToolName}", toolCall.Function.Name);
                var errorResult = JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Tool execution failed: {ex.Message}"
                });
                _conversationHistory.Add(ChatMessage.Tool(errorResult, toolCall.Id));
            }
        }
    }
    private async Task<string> ExecuteMCPToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        var originalToolName = RemovePrefix(toolName);
        foreach (var server in _mcpServers)
        {
            var tool = server.GetTool(originalToolName);
            if (tool != null)
            {
                var arguments = ParseToolArguments(argumentsJson);
                var response = await server.ExecuteToolAsync(originalToolName, arguments);
                return response.ToJson();
            }
        }
        _logger.LogWarning("Tool not found: {ToolName} (original: {OriginalToolName})", toolName, originalToolName);
        return JsonSerializer.Serialize(new
        {
            success = false,
            error = $"Tool '{toolName}' not found in any MCP server"
        });
    }
    private string RemovePrefix(string toolName)
    {
        if (toolName.StartsWith("k8s_"))
            return toolName.Substring(4);
        if (toolName.StartsWith("grafana_"))
            return toolName.Substring(8);
        if (toolName.StartsWith("mcp_"))
            return toolName.Substring(4);
        return toolName;
    }
    private Dictionary<string, object?> ParseToolArguments(string argumentsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) 
                ?? new Dictionary<string, object?>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse tool arguments: {Json}", argumentsJson);
            return new Dictionary<string, object?>();
        }
    }
    private List<ChatTool> ConvertMCPToolsToOpenAIFunctions()
    {
        var chatTools = new List<ChatTool>();
        foreach (var server in _mcpServers)
        {
            var tools = server.GetTools();
            var prefix = GetServerPrefix(server);
            foreach (var tool in tools)
            {
                var toolNameWithPrefix = $"{prefix}{tool.Name}";
                BinaryData parametersSchema;
                if (tool.RawInputSchema.HasValue)
                {
                    var rawSchema = tool.RawInputSchema.Value;
                    var fixedSchema = FixInvalidSchema(rawSchema);
                    parametersSchema = BinaryData.FromString(fixedSchema.GetRawText());
                }
                else
                {
                    var properties = new Dictionary<string, object>();
                    var required = new List<string>();
                    foreach (var param in tool.Parameters)
                    {
                        var propertySchema = new Dictionary<string, object>
                        {
                            ["type"] = param.Type
                        };
                        if (!string.IsNullOrEmpty(param.Description))
                        {
                            propertySchema["description"] = param.Description;
                        }
                        if (param.EnumValues != null && param.EnumValues.Any())
                        {
                            propertySchema["enum"] = param.EnumValues;
                        }
                        properties[param.Name] = propertySchema;
                        if (param.Required)
                        {
                            required.Add(param.Name);
                        }
                    }
                    parametersSchema = BinaryData.FromString(JsonSerializer.Serialize(new
                    {
                        type = "object",
                        properties,
                        required = required.ToArray()
                    }));
                }
                var enhancedDescription = $"[{prefix.TrimEnd('_').ToUpper()}] {tool.Description}";
                var chatTool = ChatTool.CreateFunctionTool(
                    functionName: toolNameWithPrefix,
                    functionDescription: enhancedDescription,
                    functionParameters: parametersSchema
                );
                chatTools.Add(chatTool);
                _logger.LogDebug("Registered OpenAI function: {FunctionName}", toolNameWithPrefix);
            }
        }
        _logger.LogInformation("Converted {ToolCount} MCP tools to OpenAI functions", chatTools.Count);
        return chatTools;
    }
    private string GetServerPrefix(MCPServer server)
    {
        var serverType = server.GetType().Name;
        if (serverType.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase))
        {
            return "k8s_";
        }
        if (serverType.Contains("Grafana", StringComparison.OrdinalIgnoreCase) ||
            serverType.Contains("Prometheus", StringComparison.OrdinalIgnoreCase) ||
            serverType.Contains("Loki", StringComparison.OrdinalIgnoreCase) ||
            serverType.Contains("Tempo", StringComparison.OrdinalIgnoreCase))
        {
            return "grafana_";
        }
        return "mcp_";
    }
    private JsonElement FixInvalidSchema(JsonElement schema)
    {
        var schemaDict = new Dictionary<string, object?>();
        foreach (var prop in schema.EnumerateObject())
        {
            if (prop.Name == "properties" && prop.Value.ValueKind == JsonValueKind.Object)
            {
                var fixedProps = new Dictionary<string, object?>();
                foreach (var p in prop.Value.EnumerateObject())
                {
                    fixedProps[p.Name] = FixSchemaProperty(p.Value);
                }
                schemaDict[prop.Name] = fixedProps;
            }
            else
            {
                schemaDict[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
            }
        }
        if (schemaDict.TryGetValue("type", out var typeObj) && 
            typeObj?.ToString() == "object" && 
            !schemaDict.ContainsKey("properties"))
        {
            schemaDict["properties"] = new Dictionary<string, object>();
        }
        var fixedJson = JsonSerializer.Serialize(schemaDict);
        return JsonDocument.Parse(fixedJson).RootElement;
    }
    private object? FixSchemaProperty(JsonElement prop)
    {
        if (prop.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<object>(prop.GetRawText());
        }
        var propDict = new Dictionary<string, object?>();
        foreach (var p in prop.EnumerateObject())
        {
            propDict[p.Name] = JsonSerializer.Deserialize<object>(p.Value.GetRawText());
        }
        if (propDict.TryGetValue("type", out var type) && type?.ToString() == "array")
        {
            if (!propDict.ContainsKey("items"))
            {
                propDict["items"] = new Dictionary<string, object> { ["type"] = "string" };
            }
        }
        if (propDict.TryGetValue("type", out var objType) && objType?.ToString() == "object")
        {
            if (!propDict.ContainsKey("properties"))
            {
                propDict["properties"] = new Dictionary<string, object>();
            }
        }
        return propDict;
    }
    public void ResetConversation()
    {
        _conversationHistory.Clear();
        _conversationHistory.Add(ChatMessage.System(SystemPrompt));
        _logger.LogInformation("Conversation history reset");
    }
    public int GetMessageCount() => _conversationHistory.Count;
    public List<string> GetConversationSummary()
    {
        return _conversationHistory
            .Where(m => m.Role == "user" || m.Role == "assistant")
            .Select(m => $"{m.Role}: {m.Content}")
            .ToList();
    }
}
