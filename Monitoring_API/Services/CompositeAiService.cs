namespace Monitoring_API.Services;

// Registered as the app's IClaudeService. Reads Ai:Provider (set via Settings) on every call
// and delegates to whichever concrete backend is selected ("claude" default, or "gemini") —
// so incident narratives, alerts, chat, and the trigger-build tool all support both providers
// without any of that calling code knowing which one is active.
public class CompositeAiService : IClaudeService
{
    private readonly ClaudeService _claude;
    private readonly GeminiService _gemini;
    private readonly GroqService _groq;
    private readonly SettingsService _settings;

    public CompositeAiService(ClaudeService claude, GeminiService gemini, GroqService groq, SettingsService settings)
    {
        _claude = claude;
        _gemini = gemini;
        _groq = groq;
        _settings = settings;
    }

    public async Task<string?> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens = 400, CancellationToken ct = default)
    {
        var active = await ResolveAsync(ct);
        return await active.CompleteAsync(systemPrompt, userPrompt, maxTokens, ct);
    }

    public async Task<ClaudeCompletionResult?> CompleteWithToolsAsync(
        string systemPrompt, string userPrompt, IReadOnlyList<ClaudeToolDefinition> tools,
        int maxTokens = 400, CancellationToken ct = default)
    {
        var active = await ResolveAsync(ct);
        return await active.CompleteWithToolsAsync(systemPrompt, userPrompt, tools, maxTokens, ct);
    }

    private async Task<IClaudeService> ResolveAsync(CancellationToken ct)
    {
        var provider = await _settings.GetEffectiveAsync(Models.SettingKeys.AiProvider, ct);
        return provider?.ToLowerInvariant() switch
        {
            "gemini" => _gemini,
            "groq" => _groq,
            _ => _claude
        };
    }
}
