using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monitoring_API.Services;

// A tool Claude may call instead of (or alongside) replying in plain text. InputSchema is a
// JSON-Schema-shaped object (e.g. an anonymous type) describing the tool's expected arguments.
public class ClaudeToolDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public object InputSchema { get; set; } = new { type = "object", properties = new { } };
}

// Result of a tool-enabled completion: either plain text, or Claude asking to invoke a tool
// (ToolName + ToolInput), never both. Callers must handle ToolName being set by validating the
// input against real data before acting on it — Claude's arguments are not inherently trustworthy.
public class ClaudeCompletionResult
{
    public string? Text { get; set; }
    public string? ToolName { get; set; }
    public JsonElement? ToolInput { get; set; }
}

// Thin wrapper around the Claude Messages API. Every AI feature (incident narratives,
// alert summaries, the chat assistant) goes through this one class. Both completion methods
// never throw and return null on any failure (no API key configured, timeout, HTTP error,
// malformed response) — callers MUST treat null as "fall back to existing non-AI behavior"
// so the app works identically to before when no Claude key is set.
public interface IClaudeService
{
    Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 400, CancellationToken ct = default);

    Task<ClaudeCompletionResult?> CompleteWithToolsAsync(
        string systemPrompt, string userPrompt, IReadOnlyList<ClaudeToolDefinition> tools,
        int maxTokens = 400, CancellationToken ct = default);
}

public class ClaudeService : IClaudeService
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";
    private const string DefaultModel = "claude-opus-4-8";

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<ClaudeService> _logger;

    public ClaudeService(IHttpClientFactory httpClientFactory, SettingsService settings, ILogger<ClaudeService> logger)
    {
        _http = httpClientFactory.CreateClient("claude");
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
        var apiKey = await _settings.GetEffectiveAsync(Models.SettingKeys.ClaudeApiKey, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var model = await _settings.GetEffectiveAsync(Models.SettingKeys.ClaudeModel, ct);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", ApiVersion);
            request.Content = JsonContent.Create(new ClaudeMessageRequest
            {
                Model = model,
                MaxTokens = maxTokens,
                System = systemPrompt,
                Messages = new[] { new ClaudeMessage { Role = "user", Content = userPrompt } },
                Tools = tools?.Select(t => new ClaudeToolRequest
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = t.InputSchema
                }).ToArray()
            });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Claude API returned {StatusCode}; falling back to non-AI behavior", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<ClaudeMessageResponse>(cancellationToken: ct);
            var blocks = body?.Content;
            if (blocks is null || blocks.Count == 0)
            {
                return null;
            }

            var toolUse = blocks.FirstOrDefault(c => c.Type == "tool_use");
            if (toolUse is not null)
            {
                return new ClaudeCompletionResult { ToolName = toolUse.Name, ToolInput = toolUse.Input };
            }

            var text = blocks.FirstOrDefault(c => c.Type == "text")?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : new ClaudeCompletionResult { Text = text };
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Claude API call failed; falling back to non-AI behavior");
            return null;
        }
    }

    private class ClaudeMessageRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")] public string System { get; set; } = "";
        [JsonPropertyName("messages")] public ClaudeMessage[] Messages { get; set; } = Array.Empty<ClaudeMessage>();
        [JsonPropertyName("tools")] public ClaudeToolRequest[]? Tools { get; set; }
    }

    private class ClaudeMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private class ClaudeToolRequest
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("input_schema")] public object InputSchema { get; set; } = new { };
    }

    private class ClaudeMessageResponse
    {
        [JsonPropertyName("content")] public List<ClaudeContentBlock>? Content { get; set; }
    }

    private class ClaudeContentBlock
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("input")] public JsonElement? Input { get; set; }
    }
}
