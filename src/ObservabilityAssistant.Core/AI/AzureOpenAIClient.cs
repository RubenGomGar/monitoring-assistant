using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Logging;
using ObservabilityAssistant.Core.Configuration;
using ObservabilityAssistant.Core.Models;
using OpenAI.Chat;
using ChatMessage = ObservabilityAssistant.Core.Models.ChatMessage;
namespace ObservabilityAssistant.Core.AI;
public class OpenAIService
{
    private readonly AzureOpenAIConfiguration _config;
    private readonly ILogger<OpenAIService> _logger;
    private readonly Azure.AI.OpenAI.AzureOpenAIClient _client;
    private readonly string _deploymentName;
    public OpenAIService(
        AzureOpenAIConfiguration config,
        ILogger<OpenAIService> logger)
    {
        _config = config;
        _logger = logger;
        _config.Validate();
        _client = new Azure.AI.OpenAI.AzureOpenAIClient(
            new Uri(_config.Endpoint),
            new AzureKeyCredential(_config.ApiKey));
        _deploymentName = _config.DeploymentName;
        _logger.LogInformation("Azure OpenAI Client initialized with deployment: {Deployment}", _deploymentName);
    }
    public async Task<ChatResponse> SendMessageAsync(
        List<ChatMessage> messages,
        List<ChatTool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var chatClient = _client.GetChatClient(_deploymentName);
            var chatMessages = new List<OpenAI.Chat.ChatMessage>();
            foreach (var m in messages)
            {
                OpenAI.Chat.ChatMessage msg = m.Role switch
                {
                    "system" => new SystemChatMessage(m.Content),
                    "user" => new UserChatMessage(m.Content),
                    "assistant" when m.ToolCalls != null && m.ToolCalls.Any() => 
                        new AssistantChatMessage(m.ToolCalls.Select(tc => 
                            ChatToolCall.CreateFunctionToolCall(tc.Id, tc.Function.Name, BinaryData.FromString(tc.Function.Arguments))).ToList()),
                    "assistant" => new AssistantChatMessage(m.Content),
                    "tool" => new ToolChatMessage(m.ToolCallId!, m.Content),
                    _ => throw new InvalidOperationException($"Unknown role: {m.Role}")
                };
                chatMessages.Add(msg);
            }
            var options = new ChatCompletionOptions();
            if (tools != null && tools.Count > 0)
            {
                foreach (var tool in tools)
                {
                    options.Tools.Add(tool);
                }
            }
            _logger.LogDebug("Sending {MessageCount} messages to Azure OpenAI", messages.Count);
            var response = await chatClient.CompleteChatAsync(
                chatMessages,
                options,
                cancellationToken);
            var completion = response.Value;
            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                var toolCalls = completion.ToolCalls
                    .Select(tc => new ToolCall
                    {
                        Id = tc.Id,
                        Type = "function",
                        Function = new FunctionCall
                        {
                            Name = tc.FunctionName,
                            Arguments = tc.FunctionArguments.ToString()
                        }
                    })
                    .ToList();
                _logger.LogDebug("Received {ToolCallCount} tool calls from Azure OpenAI", toolCalls.Count);
                return new ChatResponse
                {
                    Success = true,
                    ToolCalls = toolCalls
                };
            }
            var content = completion.Content.FirstOrDefault()?.Text ?? string.Empty;
            _logger.LogDebug("Received text response from Azure OpenAI: {ContentLength} chars", content.Length);
            return new ChatResponse
            {
                Success = true,
                Message = content
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure OpenAI");
            return new ChatResponse
            {
                Success = false,
                Error = $"Error: {ex.Message}"
            };
        }
    }
}
