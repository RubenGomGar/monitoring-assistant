using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.MCPServers.Base;
namespace ObservabilityAssistant.MCPServers.External;
public class ExternalMCPClient : MCPServer
{
    private readonly string _serverCommand;
    private readonly string[] _serverArgs;
    private Process? _serverProcess;
    private StreamWriter? _stdinWriter;
    private StreamReader? _stdoutReader;
    private readonly SemaphoreSlim _communicationLock = new(1, 1);
    private int _requestId = 0;
    private bool _isInitialized = false;
    public ExternalMCPClient(
        string serverCommand,
        string[] serverArgs,
        ILogger<ExternalMCPClient> logger) 
        : base(logger)
    {
        _serverCommand = serverCommand;
        _serverArgs = serverArgs;
    }
    public override async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            Logger.LogWarning("External MCP Client already initialized");
            return;
        }
        Logger.LogInformation("Initializing External MCP Server: {Command} {Args}", 
            _serverCommand, string.Join(" ", _serverArgs));
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = _serverCommand,
                Arguments = string.Join(" ", _serverArgs),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };
            _serverProcess = Process.Start(processStartInfo);
            if (_serverProcess == null)
            {
                throw new InvalidOperationException("Failed to start MCP server process");
            }
            _stdinWriter = _serverProcess.StandardInput;
            _stdinWriter.AutoFlush = true;
            _stdinWriter.NewLine = "\n";
            _stdoutReader = _serverProcess.StandardOutput;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_serverProcess.HasExited)
                    {
                        var error = await _serverProcess.StandardError.ReadLineAsync();
                        if (!string.IsNullOrEmpty(error))
                        {
                            Logger.LogWarning("MCP Server stderr: {Error}", error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error reading stderr from MCP server");
                }
            });
            await Task.Delay(1000);
            await SendInitializeRequestAsync();
            await DiscoverToolsAsync();
            _isInitialized = true;
            Logger.LogInformation("Successfully initialized External MCP Server with {ToolCount} tools", Tools.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize External MCP Server");
            await DisposeAsync();
            throw;
        }
    }
    private async Task SendInitializeRequestAsync()
    {
        var initRequest = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _requestId),
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    roots = new { listChanged = true },
                    sampling = new { }
                },
                clientInfo = new
                {
                    name = "ObservabilityAssistant",
                    version = "1.0.0"
                }
            }
        };
        var response = await SendRequestAsync(initRequest);
        if (response == null)
        {
            throw new InvalidOperationException("No response from MCP server initialize request");
        }
        if (response.Value.TryGetProperty("error", out var error))
        {
            var errorMessage = error.TryGetProperty("message", out var msg) 
                ? msg.GetString() 
                : "Unknown error";
            var errorCode = error.TryGetProperty("code", out var code)
                ? code.GetInt32()
                : -1;
            throw new InvalidOperationException($"MCP server initialization error (code {errorCode}): {errorMessage}");
        }
        if (!response.Value.TryGetProperty("result", out var result))
        {
            Logger.LogWarning("Initialize response did not contain 'result' property. Full response: {Response}", 
                response.Value.GetRawText());
        }
        var initializedNotification = new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
            @params = new { }
        };
        await SendNotificationAsync(initializedNotification);
        Logger.LogInformation("MCP Server initialized successfully");
    }
    private async Task SendNotificationAsync(object notification)
    {
        await _communicationLock.WaitAsync();
        try
        {
            if (_stdinWriter == null)
            {
                throw new InvalidOperationException("MCP server not initialized");
            }
            var notificationJson = JsonSerializer.Serialize(notification);
            Logger.LogDebug("Sending notification to MCP: {Notification}", notificationJson);
            await _stdinWriter.WriteLineAsync(notificationJson);
            await _stdinWriter.FlushAsync();
        }
        finally
        {
            _communicationLock.Release();
        }
    }
    private async Task DiscoverToolsAsync()
    {
        var listToolsRequest = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _requestId),
            method = "tools/list",
            @params = new { }
        };
        var response = await SendRequestAsync(listToolsRequest);
        if (response == null)
        {
            Logger.LogWarning("No response from tools/list request");
            return;
        }
        var result = response.Value.GetProperty("result");
        var tools = result.GetProperty("tools").EnumerateArray();
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString() ?? "unknown";
            var description = tool.GetProperty("description").GetString() ?? "";
            var parameters = new List<MCPToolParameter>();
            JsonElement? rawSchema = null;
            if (tool.TryGetProperty("inputSchema", out var schema))
            {
                rawSchema = schema;
                if (schema.TryGetProperty("properties", out var properties))
                {
                    var required = new HashSet<string>();
                    if (schema.TryGetProperty("required", out var requiredArray))
                    {
                        foreach (var req in requiredArray.EnumerateArray())
                        {
                            required.Add(req.GetString() ?? "");
                        }
                    }
                    foreach (var prop in properties.EnumerateObject())
                    {
                        var paramType = prop.Value.TryGetProperty("type", out var typeValue) 
                            ? typeValue.GetString() ?? "string" 
                            : "string";
                        var paramDesc = prop.Value.TryGetProperty("description", out var descValue)
                            ? descValue.GetString() ?? ""
                            : "";
                        parameters.Add(new MCPToolParameter
                        {
                            Name = prop.Name,
                            Type = paramType,
                            Description = paramDesc,
                            Required = required.Contains(prop.Name)
                        });
                    }
                }
            }
            var mcpTool = RegisterTool(name, description, async (args) =>
            {
                return await CallExternalToolAsync(name, args);
            }, parameters);
            if (rawSchema.HasValue)
            {
                mcpTool.RawInputSchema = rawSchema.Value;
            }
            Logger.LogDebug("Discovered external tool: {ToolName}", name);
        }
    }
    private async Task<MCPResponse> CallExternalToolAsync(string toolName, Dictionary<string, object?> arguments)
    {
        try
        {
            var callRequest = new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _requestId),
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments
                }
            };
            var response = await SendRequestAsync(callRequest);
            if (response == null)
            {
                return MCPResponse.Fail($"No response from external MCP for tool: {toolName}");
            }
            if (response.Value.TryGetProperty("error", out var error))
            {
                var errorMessage = error.GetProperty("message").GetString() ?? "Unknown error";
                return MCPResponse.Fail($"External MCP error: {errorMessage}");
            }
            var result = response.Value.GetProperty("result");
            if (result.TryGetProperty("content", out var content))
            {
                var contentArray = content.EnumerateArray().ToList();
                if (contentArray.Count > 0)
                {
                    var firstContent = contentArray[0];
                    if (firstContent.TryGetProperty("text", out var text))
                    {
                        try
                        {
                            var parsedData = JsonSerializer.Deserialize<object>(text.GetString() ?? "{}");
                            return MCPResponse.Ok(parsedData);
                        }
                        catch
                        {
                            return MCPResponse.Ok(new { text = text.GetString() });
                        }
                    }
                }
            }
            return MCPResponse.Ok(result);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error calling external tool: {ToolName}", toolName);
            return MCPResponse.Fail($"Error calling external tool: {ex.Message}");
        }
    }
    private async Task<JsonElement?> SendRequestAsync(object request)
    {
        await _communicationLock.WaitAsync();
        try
        {
            if (_stdinWriter == null || _stdoutReader == null)
            {
                throw new InvalidOperationException("MCP server not initialized");
            }
            var requestJson = JsonSerializer.Serialize(request);
            Logger.LogDebug("Sending request to MCP: {Request}", requestJson);
            await _stdinWriter.WriteLineAsync(requestJson);
            await _stdinWriter.FlushAsync();
            string? responseLine = null;
            int attempts = 0;
            const int maxAttempts = 10;
            while (attempts < maxAttempts)
            {
                responseLine = await _stdoutReader.ReadLineAsync();
                if (string.IsNullOrEmpty(responseLine))
                {
                    Logger.LogWarning("Empty line from MCP server (attempt {Attempt})", attempts + 1);
                    attempts++;
                    continue;
                }
                if (responseLine.TrimStart().StartsWith("{"))
                {
                    break;
                }
                Logger.LogDebug("Skipping non-JSON line from MCP: {Line}", responseLine);
                attempts++;
            }
            if (string.IsNullOrEmpty(responseLine))
            {
                Logger.LogWarning("No valid JSON response from MCP server after {Attempts} attempts", maxAttempts);
                return null;
            }
            Logger.LogDebug("Received response from MCP: {Response}", responseLine);
            var response = JsonSerializer.Deserialize<JsonElement>(responseLine);
            return response;
        }
        finally
        {
            _communicationLock.Release();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_serverProcess != null && !_serverProcess.HasExited)
        {
            try
            {
                _stdinWriter?.Close();
                _serverProcess.Kill(entireProcessTree: true);
                await _serverProcess.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error disposing MCP server process");
            }
        }
        _stdinWriter?.Dispose();
        _stdoutReader?.Dispose();
        _serverProcess?.Dispose();
        _communicationLock.Dispose();
    }
}
