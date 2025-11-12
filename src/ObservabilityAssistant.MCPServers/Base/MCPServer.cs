using Microsoft.Extensions.Logging;
namespace ObservabilityAssistant.MCPServers.Base;
public abstract class MCPServer
{
    protected readonly ILogger Logger;
    protected readonly List<MCPTool> Tools = new();
    protected MCPServer(ILogger logger)
    {
        Logger = logger;
    }
    public abstract Task InitializeAsync();
    public List<MCPTool> GetTools() => Tools;
    public MCPTool? GetTool(string name) => Tools.FirstOrDefault(t => t.Name == name);
    public async Task<MCPResponse> ExecuteToolAsync(string toolName, Dictionary<string, object?> arguments)
    {
        try
        {
            var tool = GetTool(toolName);
            if (tool == null)
            {
                Logger.LogWarning("Tool not found: {ToolName}", toolName);
                return MCPResponse.Fail($"Tool '{toolName}' not found");
            }
            Logger.LogInformation("Executing tool: {ToolName} with {ArgCount} arguments", 
                toolName, arguments.Count);
            var missingParams = tool.Parameters
                .Where(p => p.Required && !arguments.ContainsKey(p.Name))
                .Select(p => p.Name)
                .ToList();
            if (missingParams.Any())
            {
                return MCPResponse.Fail($"Missing required parameters: {string.Join(", ", missingParams)}");
            }
            var result = await tool.Handler(arguments);
            Logger.LogInformation("Tool {ToolName} executed successfully: {Success}", 
                toolName, result.Success);
            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error executing tool: {ToolName}", toolName);
            return MCPResponse.Fail($"Error executing tool: {ex.Message}");
        }
    }
    protected MCPTool RegisterTool(
        string name,
        string description,
        Func<Dictionary<string, object?>, Task<MCPResponse>> handler,
        List<MCPToolParameter>? parameters = null)
    {
        var tool = new MCPTool
        {
            Name = name,
            Description = description,
            Parameters = parameters ?? new List<MCPToolParameter>(),
            Handler = handler
        };
        Tools.Add(tool);
        Logger.LogDebug("Registered tool: {ToolName}", name);
        return tool;
    }
    protected static Dictionary<string, object?> ParseArguments(string argumentsJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson) 
                ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }
}
