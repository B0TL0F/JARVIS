using System.Text.Json;

namespace Monitoring_API.Models;

public class ChatRequestDto
{
    public string Question { get; set; } = string.Empty;
    public string? Environment { get; set; }
}

public class ChatResponseDto
{
    public string Answer { get; set; } = string.Empty;
    public bool ClaudeConfigured { get; set; }
    // Set only when the AI identified a mutating action and matched it against real data —
    // the UI must show an explicit confirm/cancel step before POSTing to /api/chat/confirm.
    // Never auto-executed server-side.
    public PendingActionDto? PendingAction { get; set; }
}

// Generic pending-action envelope: Type selects which mutation /api/chat/confirm performs,
// Params carries whatever that mutation needs (already validated against real data server-side
// before this was ever returned to the client — see ChatController's per-tool resolution).
public class PendingActionDto
{
    public string Type { get; set; } = "";
    public Dictionary<string, JsonElement> Params { get; set; } = new();
}

// What the UI posts back on Confirm — Params round-trips exactly what PendingActionDto sent it,
// but the server re-validates everything again here rather than trusting the client payload.
public class ChatConfirmRequest
{
    public string Type { get; set; } = "";
    public Dictionary<string, JsonElement> Params { get; set; } = new();
}

public class ChatConfirmResponseDto
{
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
}
