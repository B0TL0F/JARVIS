using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monitoring_API.Services;

// Gemini-backed implementation of IClaudeService, using Google's generateContent REST API
// (https://generativelanguage.googleapis.com). Implements the exact same contract as
// ClaudeService — CompleteAsync/CompleteWithToolsAsync never throw, return null on any
// failure (no key configured, timeout, HTTP error, malformed response) — so every existing
// caller (incident narratives, alerts, chat, the trigger-build tool) works unchanged
// regardless of which provider is selected.
public class GeminiService : IClaudeService
{
    private const string ApiUrlTemplate = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent?key={1}";
    private const string DefaultModel = "gemini-2.0-flash";

    private readonly HttpClient _http;
    private readonly SettingsService _settings;
    private readonly ILogger<GeminiService> _logger;

    public GeminiService(IHttpClientFactory httpClientFactory, SettingsService settings, ILogger<GeminiService> logger)
    {
        _http = httpClientFactory.CreateClient("gemini");
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
        var apiKey = await _settings.GetEffectiveAsync(Models.SettingKeys.GeminiApiKey, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var model = await _settings.GetEffectiveAsync(Models.SettingKeys.GeminiModel, ct);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        try
        {
            var url = string.Format(ApiUrlTemplate, model, apiKey);

            var body = new GeminiRequest
            {
                SystemInstruction = new GeminiContent { Parts = new[] { new GeminiPart { Text = systemPrompt } } },
                Contents = new[] { new GeminiContent { Role = "user", Parts = new[] { new GeminiPart { Text = userPrompt } } } },
                GenerationConfig = new GeminiGenerationConfig { MaxOutputTokens = maxTokens },
                Tools = tools is { Count: > 0 }
                    ? new[] { new GeminiToolDeclaration { FunctionDeclarations = tools.Select(t => new GeminiFunctionDeclaration
                        {
                            Name = t.Name,
                            Description = t.Description,
                            Parameters = t.InputSchema
                        }).ToArray() } }
                    : null
            };

            using var response = await _http.PostAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini API returned {StatusCode}; falling back to non-AI behavior", response.StatusCode);
                return null;
            }

            var parsed = await response.Content.ReadFromJsonAsync<GeminiResponse>(cancellationToken: ct);
            var parts = parsed?.Candidates?.FirstOrDefault()?.Content?.Parts;
            if (parts is null || parts.Length == 0)
            {
                return null;
            }

            var functionCall = parts.FirstOrDefault(p => p.FunctionCall is not null)?.FunctionCall;
            if (functionCall is not null)
            {
                var argsJson = JsonSerializer.Serialize(functionCall.Args ?? new Dictionary<string, object>());
                var argsElement = JsonSerializer.Deserialize<JsonElement>(argsJson);
                return new ClaudeCompletionResult { ToolName = functionCall.Name, ToolInput = argsElement };
            }

            var text = parts.FirstOrDefault(p => p.Text is not null)?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : new ClaudeCompletionResult { Text = text };
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Gemini API call failed; falling back to non-AI behavior");
            return null;
        }
    }

    private class GeminiRequest
    {
        [JsonPropertyName("system_instruction")] public GeminiContent? SystemInstruction { get; set; }
        [JsonPropertyName("contents")] public GeminiContent[] Contents { get; set; } = Array.Empty<GeminiContent>();
        [JsonPropertyName("generationConfig")] public GeminiGenerationConfig? GenerationConfig { get; set; }
        [JsonPropertyName("tools")] public GeminiToolDeclaration[]? Tools { get; set; }
    }

    private class GeminiContent
    {
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("parts")] public GeminiPart[] Parts { get; set; } = Array.Empty<GeminiPart>();
    }

    private class GeminiPart
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("functionCall")] public GeminiFunctionCall? FunctionCall { get; set; }
    }

    private class GeminiGenerationConfig
    {
        [JsonPropertyName("maxOutputTokens")] public int MaxOutputTokens { get; set; }
    }

    private class GeminiToolDeclaration
    {
        [JsonPropertyName("functionDeclarations")] public GeminiFunctionDeclaration[] FunctionDeclarations { get; set; } = Array.Empty<GeminiFunctionDeclaration>();
    }

    private class GeminiFunctionDeclaration
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("parameters")] public object Parameters { get; set; } = new { };
    }

    private class GeminiFunctionCall
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("args")] public Dictionary<string, object>? Args { get; set; }
    }

    private class GeminiResponse
    {
        [JsonPropertyName("candidates")] public List<GeminiCandidate>? Candidates { get; set; }
    }

    private class GeminiCandidate
    {
        [JsonPropertyName("content")] public GeminiContent? Content { get; set; }
    }
}
