using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monitoring_API.Services;

// Groq-backed implementation of IClaudeService. Groq exposes an OpenAI-compatible Chat
// Completions API (https://api.groq.com/openai/v1/chat/completions) — free tier, no billing
// required, running open-weight models like Llama 3.3 70B. Implements the exact same contract
// as ClaudeService/GeminiService — never throws, returns null on any failure — so every
// existing caller works unchanged regardless of which provider is selected.
public class GroqService : IClaudeService
{
    private const string ApiUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string DefaultModel = "llama-3.3-70b-versatile";

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<GroqService> _logger;

    public GroqService(IHttpClientFactory httpClientFactory, SettingsService settings, ILogger<GroqService> logger)
    {
        _http = httpClientFactory.CreateClient("groq");
        _settings = settings;
        _logger = logger;
    }

    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 400, CancellationToken ct = default)
    {
        var result = await SendAsync(systemPrompt, userPrompt, tools: null, maxTokens, ct);
        return result?.Text;
    }

    public async Task<ClaudeCompletionResult?> CompleteWithToolsAsync(
        string systemPrompt, string userPrompt, IReadOnlyList<ClaudeToolDefinition> tools,
        int maxTokens = 400, CancellationToken ct = default)
    {
        return await SendAsync(systemPrompt, userPrompt, tools, maxTokens, ct);
    }

    private async Task<ClaudeCompletionResult?> SendAsync(
        string systemPrompt, string userPrompt, IReadOnlyList<ClaudeToolDefinition>? tools,
        int maxTokens, CancellationToken ct)
    {
        var apiKey = await _settings.GetEffectiveAsync(Models.SettingKeys.GroqApiKey, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var model = await _settings.GetEffectiveAsync(Models.SettingKeys.GroqModel, ct);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new GroqRequest
            {
                Model = model,
                MaxTokens = maxTokens,
                Messages = new[]
                {
                    new GroqMessage { Role = "system", Content = systemPrompt },
                    new GroqMessage { Role = "user", Content = userPrompt }
                },
                Tools = tools is { Count: > 0 }
                    ? tools.Select(t => new GroqTool
                    {
                        Function = new GroqFunctionDeclaration
                        {
                            Name = t.Name,
                            Description = t.Description,
                            Parameters = t.InputSchema
                        }
                    }).ToArray()
                    : null
            });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Groq API returned {StatusCode}; falling back to non-AI behavior. Body: {Body}", response.StatusCode, errorBody);
                return null;
            }

            var parsed = await response.Content.ReadFromJsonAsync<GroqResponse>(cancellationToken: ct);
            var message = parsed?.Choices?.FirstOrDefault()?.Message;
            if (message is null)
            {
                return null;
            }

            var toolCall = message.ToolCalls?.FirstOrDefault();
            if (toolCall is not null)
            {
                var argsElement = JsonSerializer.Deserialize<JsonElement>(
                    string.IsNullOrWhiteSpace(toolCall.Function.Arguments) ? "{}" : toolCall.Function.Arguments);
                return new ClaudeCompletionResult { ToolName = toolCall.Function.Name, ToolInput = argsElement };
            }

            return string.IsNullOrWhiteSpace(message.Content) ? null : new ClaudeCompletionResult { Text = message.Content };
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Groq API call failed; falling back to non-AI behavior");
            return null;
        }
    }

    private class GroqRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("messages")] public GroqMessage[] Messages { get; set; } = Array.Empty<GroqMessage>();
        [JsonPropertyName("tools")] public GroqTool[]? Tools { get; set; }
    }

    private class GroqMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<GroqToolCall>? ToolCalls { get; set; }
    }

    private class GroqTool
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public GroqFunctionDeclaration Function { get; set; } = new();
    }

    private class GroqFunctionDeclaration
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("parameters")] public object Parameters { get; set; } = new { };
    }

    private class GroqToolCall
    {
        [JsonPropertyName("function")] public GroqToolCallFunction Function { get; set; } = new();
    }

    private class GroqToolCallFunction
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("arguments")] public string Arguments { get; set; } = "{}";
    }

    private class GroqResponse
    {
        [JsonPropertyName("choices")] public List<GroqChoice>? Choices { get; set; }
    }

    private class GroqChoice
    {
        [JsonPropertyName("message")] public GroqMessage? Message { get; set; }
    }
}
