using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.MCPServers.Base;

namespace ObservabilityAssistant.MCPServers.External;

/// <summary>
/// Cliente MCP que se conecta a un servidor MCP remoto via HTTP (streamable-http transport)
/// </summary>
public class HttpMCPClient : MCPServer
{
    private readonly HttpClient _httpClient;
    private readonly string _mcpEndpoint;
    private int _requestId = 0;
    private bool _isInitialized = false;
    private string? _sessionId = null;
    private readonly Dictionary<string, JsonElement> _remoteToolSchemas = new();

    public HttpMCPClient(
        string baseUrl,
        string endpointPath,
        ILogger<HttpMCPClient> logger) 
        : base(logger)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _mcpEndpoint = endpointPath.TrimStart('/');
    }

    public override async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            Logger.LogWarning("HTTP MCP Client already initialized");
            return;
        }

        Logger.LogInformation("Initializing HTTP MCP Client: {BaseUrl}/{Endpoint}", 
            _httpClient.BaseAddress, _mcpEndpoint);

        try
        {
            // Enviar request de inicialización directamente (no hay health check simple en streamable-http)
            await SendInitializeRequestAsync();

            // Descubrir herramientas disponibles
            await DiscoverToolsAsync();

            _isInitialized = true;
            Logger.LogInformation(
                "Successfully initialized HTTP MCP Client with {ToolCount} tools", 
                Tools.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize HTTP MCP Client");
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
                    tools = new { }
                },
                clientInfo = new
                {
                    name = "ObservabilityAssistant",
                    version = "1.0.0"
                }
            }
        };

        var (response, httpResponse) = await SendRequestWithResponseAsync(initRequest);
        
        // Capturar el session ID si el servidor lo proporciona
        if (httpResponse?.Headers.TryGetValues("Mcp-Session-Id", out var sessionIdValues) == true)
        {
            _sessionId = sessionIdValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(_sessionId))
            {
                Logger.LogInformation("Session ID received from MCP server: {SessionId}", _sessionId);
            }
        }
        
        if (response == null || !response.RootElement.TryGetProperty("result", out _))
        {
            throw new InvalidOperationException("Failed to initialize MCP connection");
        }

        Logger.LogInformation("MCP Server initialized successfully");

        // Enviar initialized notification
        var initializedNotification = new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        };

        await SendNotificationAsync(initializedNotification);
    }

    private async Task DiscoverToolsAsync()
    {
        var listToolsRequest = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _requestId),
            method = "tools/list"
        };

        var response = await SendRequestAsync(listToolsRequest);

        if (response == null)
        {
            Logger.LogWarning("No response received when listing tools");
            return;
        }

        if (response.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("tools", out var toolsArray))
        {
            foreach (var toolElement in toolsArray.EnumerateArray())
            {
                try
                {
                    var tool = ParseToolFromJson(toolElement);
                    if (tool != null)
                    {
                        Tools.Add(tool);
                        Logger.LogDebug("Discovered tool: {ToolName}", tool.Name);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to parse tool from response");
                }
            }
        }

        Logger.LogInformation("Discovered {Count} tools from MCP server", Tools.Count);
    }

    private MCPTool? ParseToolFromJson(JsonElement toolElement)
    {
        if (!toolElement.TryGetProperty("name", out var nameElement))
            return null;

        var name = nameElement.GetString();
        if (string.IsNullOrEmpty(name))
            return null;

        var description = toolElement.TryGetProperty("description", out var descElement)
            ? descElement.GetString() ?? ""
            : "";

        // Guardar el schema para uso posterior
        if (toolElement.TryGetProperty("inputSchema", out var schemaElement))
        {
            _remoteToolSchemas[name] = schemaElement;
        }

        var tool = new MCPTool
        {
            Name = name,
            Description = description,
            Parameters = new List<MCPToolParameter>(),
            Handler = async (args) => await ExecuteRemoteToolAsync(name, args)
        };

        if (toolElement.TryGetProperty("inputSchema", out var schema))
        {
            tool.RawInputSchema = schema;
        }

        return tool;
    }

    private async Task<MCPResponse> ExecuteRemoteToolAsync(string toolName, Dictionary<string, object?> arguments)
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("MCP Client not initialized");
        }

        Logger.LogInformation("Executing remote tool: {ToolName}", toolName);

        try
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _requestId),
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments = arguments
                }
            };

            var response = await SendRequestAsync(request);

            if (response == null)
            {
                return MCPResponse.Fail("No response from MCP server");
            }

            return ParseMCPResponse(response);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error executing remote tool {ToolName}", toolName);
            return MCPResponse.Fail($"Error: {ex.Message}");
        }
    }

    private async Task<JsonDocument?> SendRequestAsync(object request)
    {
        var (jsonDoc, _) = await SendRequestWithResponseAsync(request);
        return jsonDoc;
    }

    private async Task<(JsonDocument?, HttpResponseMessage?)> SendRequestWithResponseAsync(object request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request);
            Logger.LogDebug("Sending request to MCP server: {Request}", json);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Crear el request message para poder agregar headers
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, _mcpEndpoint)
            {
                Content = content
            };
            
            // Agregar headers requeridos
            requestMessage.Headers.Add("Accept", "application/json");
            requestMessage.Headers.Add("Accept", "text/event-stream");
            
            // Si tenemos session ID, incluirlo en el header
            if (!string.IsNullOrEmpty(_sessionId))
            {
                requestMessage.Headers.Add("Mcp-Session-Id", _sessionId);
                Logger.LogDebug("Including session ID in request: {SessionId}", _sessionId);
            }

            var response = await _httpClient.SendAsync(requestMessage);
            
            var responseBody = await response.Content.ReadAsStringAsync();
            Logger.LogDebug("MCP server response ({StatusCode}): {Response}", 
                response.StatusCode, responseBody);
            
            response.EnsureSuccessStatusCode();
            
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                Logger.LogWarning("Empty response from MCP server");
                return (null, response);
            }

            return (JsonDocument.Parse(responseBody), response);
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP error communicating with MCP server");
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error sending request to MCP server");
            throw;
        }
    }

    private async Task SendNotificationAsync(object notification)
    {
        try
        {
            var json = JsonSerializer.Serialize(notification);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var requestMessage = new HttpRequestMessage(HttpMethod.Post, _mcpEndpoint)
            {
                Content = content
            };
            
            // Si tenemos session ID, incluirlo en el header
            if (!string.IsNullOrEmpty(_sessionId))
            {
                requestMessage.Headers.Add("Mcp-Session-Id", _sessionId);
            }
            
            await _httpClient.SendAsync(requestMessage);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error sending notification to MCP server");
        }
    }

    private MCPResponse ParseMCPResponse(JsonDocument response)
    {
        if (response.RootElement.TryGetProperty("error", out var errorElement))
        {
            var errorMessage = errorElement.TryGetProperty("message", out var msgElement)
                ? msgElement.GetString()
                : "Unknown error";

            return MCPResponse.Fail(errorMessage ?? "Unknown error");
        }

        if (!response.RootElement.TryGetProperty("result", out var result))
        {
            return MCPResponse.Fail("Invalid response format");
        }

        // Intentar extraer el contenido de la respuesta
        if (result.TryGetProperty("content", out var contentArray))
        {
            var contentList = new List<object>();
            foreach (var item in contentArray.EnumerateArray())
            {
                if (item.TryGetProperty("text", out var textElement))
                {
                    contentList.Add(textElement.GetString() ?? "");
                }
                else
                {
                    contentList.Add(item.GetRawText());
                }
            }
            
            return MCPResponse.Ok(contentList.Count == 1 ? contentList[0] : contentList);
        }

        // Si no hay content array, devolver el resultado completo
        return MCPResponse.Ok(result.GetRawText());
    }
}
