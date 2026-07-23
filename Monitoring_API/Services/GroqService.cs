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

    // Fallback model tried when the configured/primary model hits a rate limit (429, or Groq's
    // "RequestEntityTooLarge" TPM-exceeded response) — Groq's free-tier limits are per-model, so
    // a different model is very likely NOT currently rate-limited even when the primary is.
    // Picked as each other's complement: 70B has far better per-request quality/TPM headroom but
    // a much lower daily token budget (100K TPD); 8B is the opposite (500K TPD, but only 6K TPM
    // — chokes on any single large request). Falling back to whichever one you're NOT primarily
    // using covers both failure shapes without needing a third model.
    private const string FallbackModelForLarge = "llama-3.1-8b-instant";
    private const string FallbackModelForSmall = "llama-3.3-70b-versatile";

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

        var (result, rateLimited) = await SendOnceAsync(apiKey, model, systemPrompt, userPrompt, tools, maxTokens, ct);
        if (result is not null || !rateLimited) return result;

        // The primary model is currently rate-limited (per-minute token size, or daily budget
        // exhausted) — try the complementary model once before giving up entirely, since Groq's
        // free-tier limits are tracked per-model and the other one is very likely not affected.
        var fallbackModel = model.Equals(FallbackModelForLarge, StringComparison.OrdinalIgnoreCase)
            ? FallbackModelForSmall
            : FallbackModelForLarge;
        _logger.LogInformation("Groq model '{Model}' rate-limited — retrying with fallback '{Fallback}'", model, fallbackModel);
        var (fallbackResult, _) = await SendOnceAsync(apiKey, fallbackModel, systemPrompt, userPrompt, tools, maxTokens, ct);
        return fallbackResult;
    }

    // Returns (result, wasRateLimited). wasRateLimited distinguishes "the model is temporarily
    // over its quota — worth trying a different model" from any other failure (bad key, model
    // doesn't support tool calls, network error) where retrying with a different model wouldn't
    // help and would just waste another request.
    private async Task<(ClaudeCompletionResult? Result, bool RateLimited)> SendOnceAsync(
        string apiKey, string model, string systemPrompt, string userPrompt,
        IReadOnlyList<ClaudeToolDefinition>? tools, int maxTokens, CancellationToken ct)
    {
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
                // 429 = standard rate limit; Groq also returns 413 RequestEntityTooLarge for a
                // single request exceeding a model's per-minute token limit — both mean "this
                // model specifically is over budget right now", not "the request is broken".
                var rateLimited = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    || response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge;
                _logger.LogWarning("Groq API ({Model}) returned {StatusCode}; falling back to non-AI behavior. Body: {Body}", model, response.StatusCode, errorBody);
                return (null, rateLimited);
            }

            var parsed = await response.Content.ReadFromJsonAsync<GroqResponse>(cancellationToken: ct);
            var message = parsed?.Choices?.FirstOrDefault()?.Message;
            if (message is null)
            {
                return (null, false);
            }

            var toolCall = message.ToolCalls?.FirstOrDefault();
            if (toolCall is not null)
            {
                var argsElement = JsonSerializer.Deserialize<JsonElement>(
                    string.IsNullOrWhiteSpace(toolCall.Function.Arguments) ? "{}" : toolCall.Function.Arguments);
                return (new ClaudeCompletionResult { ToolName = toolCall.Function.Name, ToolInput = argsElement }, false);
            }

            return (string.IsNullOrWhiteSpace(message.Content) ? null : new ClaudeCompletionResult { Text = message.Content }, false);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Groq API call ({Model}) failed; falling back to non-AI behavior", model);
            return (null, false);
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
