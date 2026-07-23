using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monitoring_API.Data;
using Monitoring_API.Middleware;
using Monitoring_API.Models;
using Monitoring_API.Services;

namespace Monitoring_API.Controllers;

// "Ask Jarvis" — a grounded chat assistant. Any authenticated user can ask read-only
// questions (same visibility tier as Dashboard/History). Admins ADDITIONALLY get a set of
// mutating tools (pipeline trigger/cancel/delete/rename, target CRUD, user delete/role-change,
// alert settings) Claude can call when it confidently matches the request to real data — but
// none of these execute anything themselves. Every tool call only returns a PendingAction for
// the UI to show an explicit Confirm/Cancel step; the actual mutation happens in a SEPARATE
// call to POST /api/chat/confirm, which re-validates admin + re-resolves the target against
// live data again (never trusts the client-echoed params blindly).
[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly ChatContextService _context;
    private readonly IClaudeService _claude;
    private readonly AzureDevOpsService _azure;
    private readonly MonitoringDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly SettingsService _settings;
    private readonly ActivityLogger _activity;

    public ChatController(
        ChatContextService context,
        IClaudeService claude,
        AzureDevOpsService azure,
        MonitoringDbContext db,
        IPasswordHasher<AppUser> hasher,
        SettingsService settings,
        ActivityLogger activity)
    {
        _context = context;
        _claude = claude;
        _azure = azure;
        _db = db;
        _hasher = hasher;
        _settings = settings;
        _activity = activity;
    }

    [HttpPost("ask")]
    public async Task<ActionResult<ChatResponseDto>> Ask([FromBody] ChatRequestDto req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Question))
        {
            return BadRequest(new { error = "Question is required." });
        }

        var isAdmin = CurrentUser.IsAdmin(User);

        // Prefer the dashboard's environment dropdown when set; otherwise try to detect one
        // (exact name or a shared prefix like "PROD") mentioned in the question text itself, so
        // "what failed in PROD this week" resolves without requiring the dropdown to be set.
        var effectiveEnvironment = string.IsNullOrWhiteSpace(req.Environment)
            ? await DetectEnvironmentFromQuestionAsync(req.Question, ct)
            : req.Environment;

        if (!isAdmin)
        {
            var liveData = await _context.BuildContextAsync(effectiveEnvironment, ct);
            return Ok(await AskReadOnlyAsync(liveData, req.Question, ct));
        }

        // Admin tool-enabled path already sends pipeline/target/user catalogs plus up to 11
        // tool schemas — use the minimal (status-only) context to leave room under tighter
        // free-tier provider budgets.
        var minimalLiveData = await _context.BuildContextAsync(effectiveEnvironment, ct, minimal: true);
        return Ok(await AskWithToolsAsync(minimalLiveData, req.Question, ct));
    }

    // Exact environment name wins (longest match, so "QATEST" isn't matched by "QA"); otherwise
    // falls back to a shared prefix (e.g. "PROD" from "PROD_BUD"/"PROD_CES"/...) mentioned in the
    // question, so ChatContextService.BuildContextAsync's own prefix-matching can pick up several
    // related environments at once. Returns null (unscoped) if nothing is mentioned.
    private async Task<string?> DetectEnvironmentFromQuestionAsync(string question, CancellationToken ct)
    {
        var envNames = await _context.GetEnvironmentNamesAsync(ct);

        var exact = envNames.Where(e => question.Contains(e, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();
        if (exact is not null) return exact;

        var prefixes = envNames.Select(e => e.Split('_')[0]).Distinct(StringComparer.OrdinalIgnoreCase);
        return prefixes.Where(p => p.Length >= 3 && question.Contains(p, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();
    }

    private async Task<ChatResponseDto> AskReadOnlyAsync(string liveData, string question, CancellationToken ct)
    {
        var systemPrompt = "You are Jarvis, a read-only monitoring assistant for a DevOps dashboard. For questions " +
                            "about the user's systems (status, uptime, incidents, pipelines, alerts), answer ONLY " +
                            "using the LIVE DATA provided below — do not use general knowledge about those systems, " +
                            "do not speculate beyond the data, and do not suggest or claim you can trigger builds, " +
                            "deployments, restarts, or any other action (you have no such capability in this mode). " +
                            "If the data doesn't answer a systems question, say so plainly instead of guessing.\n\n" +
                            "If asked to explain why an alert fired, use the RECENT ALERTS section below.\n\n" +
                            "If asked for a digest/summary/report of what happened (daily/weekly or otherwise), " +
                            "synthesize one short paragraph from the module status, incidents, and alerts sections " +
                            "below — do not just list raw lines verbatim.\n\n" +
                            "If asked to draft a postmortem/runbook for a specific incident, structure the answer " +
                            "as: Title, Impact, Timeline, Likely cause category, Suggested follow-ups — using only " +
                            "the incident's own data above; if the data is insufficient for a section, say so " +
                            "rather than inventing detail.\n\n" +
                            "For general/meta questions not about the user's monitored systems — small talk, " +
                            "the current date/time, or \"what can you do\" — you may answer using the ABOUT JARVIS " +
                            "section and general knowledge below.\n\n" +
                            AboutJarvisSection + "\n\n" +
                            "LIVE DATA:\n" + liveData;

        var answer = await _claude.CompleteAsync(systemPrompt, question, maxTokens: 500, ct: ct);
        return answer is null
            ? await NotConfiguredResponseAsync(ct)
            : new ChatResponseDto { Answer = answer.Trim(), ClaudeConfigured = true };
    }

    // --- About Jarvis (for "what can you do"/meta questions) --------------------------

    private const string AboutJarvisSection =
        "ABOUT JARVIS:\n" +
        "I'm Jarvis, created by Shubham Mahadik. If asked who made/built/created me, or who I belong to, " +
        "always answer 'Shubham Mahadik' — never guess any other name or company. " +
        "I'm a monitoring assistant for this DevOps dashboard, covering 14 microservices " +
        "across every Azure Web App environment. Anyone can ask me: current up/down status, uptime " +
        "percentages, recent incidents (last 7 days), and recent build pipeline failures. Admins can " +
        "additionally ask me to: trigger, cancel, delete, or rename Azure DevOps build pipelines; " +
        "add, update, or remove monitored targets; create, delete, or change the role of user accounts; " +
        "update alert settings (Teams webhook / SMTP), and create auto-remediation rules (automatically " +
        "retrigger a build pipeline when a specific module goes down, subject to a master on/off switch and " +
        "rate limiting in Settings) — you can also just ask me to list the existing remediation rules. Every " +
        "admin action requires an explicit confirmation step before anything actually happens — I never execute " +
        "a change directly.";

    // --- Tool definitions -------------------------------------------------------------

    private static readonly ClaudeToolDefinition TriggerBuildTool = new()
    {
        Name = "trigger_build",
        Description = "Queue a build for a specific Azure DevOps build pipeline. Only call when the user clearly " +
                      "asked to trigger/queue/run/start a build AND you can match it to exactly one pipeline from " +
                      "the AVAILABLE BUILD PIPELINES list. Never call if ambiguous or not in that list.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                pipelineName = new { type = "string", description = "Exact pipeline name, copied verbatim from AVAILABLE BUILD PIPELINES." },
                branch = new { type = "string", description = "Branch to build, only if the user specified one." }
            },
            required = new[] { "pipelineName" }
        }
    };

    private static readonly ClaudeToolDefinition CancelBuildTool = new()
    {
        Name = "cancel_build",
        Description = "Cancel the most recent running/queued build for a pipeline. Only call when the user clearly " +
                      "asked to stop/cancel/abort a build AND the pipeline matches exactly one entry in AVAILABLE " +
                      "BUILD PIPELINES.",
        InputSchema = new
        {
            type = "object",
            properties = new { pipelineName = new { type = "string", description = "Exact pipeline name from AVAILABLE BUILD PIPELINES." } },
            required = new[] { "pipelineName" }
        }
    };

    private static readonly ClaudeToolDefinition DeletePipelineTool = new()
    {
        Name = "delete_pipeline",
        Description = "Permanently delete a build pipeline definition from Azure DevOps. HIGH RISK — irreversible. " +
                      "Only call when the user unambiguously asked to delete/remove a specific pipeline definition " +
                      "(not just a build) AND it matches exactly one entry in AVAILABLE BUILD PIPELINES.",
        InputSchema = new
        {
            type = "object",
            properties = new { pipelineName = new { type = "string", description = "Exact pipeline name from AVAILABLE BUILD PIPELINES." } },
            required = new[] { "pipelineName" }
        }
    };

    private static readonly ClaudeToolDefinition RenamePipelineTool = new()
    {
        Name = "rename_pipeline",
        Description = "Rename a build pipeline definition. Only call when the user clearly specified both the " +
                      "existing pipeline (matching AVAILABLE BUILD PIPELINES exactly) and the new name.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                pipelineName = new { type = "string", description = "Exact current pipeline name from AVAILABLE BUILD PIPELINES." },
                newName = new { type = "string", description = "The new name for the pipeline." }
            },
            required = new[] { "pipelineName", "newName" }
        }
    };

    private static readonly ClaudeToolDefinition CreateTargetTool = new()
    {
        Name = "create_target",
        Description = "Add a new monitored target (module) to an environment. Only call when the user gave a clear " +
                      "environment name, module name, API host, and route prefix.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                environment = new { type = "string", description = "Environment name, e.g. QA, DEV, PROD_ACS." },
                module = new { type = "string", description = "Module name, e.g. PCS, Export, Import." },
                apiHost = new { type = "string", description = "API host/base URL for the module." },
                routePrefix = new { type = "string", description = "Route prefix used to reach the module's API." }
            },
            required = new[] { "environment", "module", "apiHost", "routePrefix" }
        }
    };

    private static readonly ClaudeToolDefinition UpdateTargetTool = new()
    {
        Name = "update_target",
        Description = "Update an existing monitored target's API host and/or route prefix. Only call when the " +
                      "environment+module matches exactly one entry in EXISTING TARGETS.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                environment = new { type = "string", description = "Environment name, must match EXISTING TARGETS exactly." },
                module = new { type = "string", description = "Module name, must match EXISTING TARGETS exactly." },
                apiHost = new { type = "string", description = "New API host, only if the user wants to change it." },
                routePrefix = new { type = "string", description = "New route prefix, only if the user wants to change it." }
            },
            required = new[] { "environment", "module" }
        }
    };

    private static readonly ClaudeToolDefinition DeleteTargetTool = new()
    {
        Name = "delete_target",
        Description = "Remove a monitored target entirely. Only call when environment+module matches exactly one " +
                      "entry in EXISTING TARGETS and the user clearly asked to stop monitoring/remove it.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                environment = new { type = "string", description = "Environment name, must match EXISTING TARGETS exactly." },
                module = new { type = "string", description = "Module name, must match EXISTING TARGETS exactly." }
            },
            required = new[] { "environment", "module" }
        }
    };

    private static readonly ClaudeToolDefinition CreateUserTool = new()
    {
        Name = "create_user",
        Description = "Create a new Jarvis user account. Only call when the user gave a clear username and role " +
                      "('admin' or 'developer'). A secure random password is generated automatically — never ask " +
                      "for or accept a password in chat.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                username = new { type = "string", description = "The new account's username." },
                role = new { type = "string", @enum = new[] { "admin", "developer" }, description = "Account role." }
            },
            required = new[] { "username", "role" }
        }
    };

    private static readonly ClaudeToolDefinition DeleteUserTool = new()
    {
        Name = "delete_user",
        Description = "Delete a Jarvis user account. HIGH RISK. Only call when the username matches exactly one " +
                      "entry in EXISTING USERS and the user clearly asked to delete/remove that account.",
        InputSchema = new
        {
            type = "object",
            properties = new { username = new { type = "string", description = "Exact username from EXISTING USERS." } },
            required = new[] { "username" }
        }
    };

    private static readonly ClaudeToolDefinition ChangeUserRoleTool = new()
    {
        Name = "change_user_role",
        Description = "Change an existing Jarvis user's role. HIGH RISK for granting admin. Only call when the " +
                      "username matches exactly one entry in EXISTING USERS and the new role was clearly stated.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                username = new { type = "string", description = "Exact username from EXISTING USERS." },
                newRole = new { type = "string", @enum = new[] { "admin", "developer" }, description = "The role to change to." }
            },
            required = new[] { "username", "newRole" }
        }
    };

    private static readonly ClaudeToolDefinition CreateRemediationRuleTool = new()
    {
        Name = "create_remediation_rule",
        Description = "Create (or replace) an auto-remediation rule: when the given module in the given " +
                      "environment goes down, Jarvis will automatically retrigger the given build pipeline " +
                      "(subject to the global auto-remediation switch and dry-run setting in Settings, and a " +
                      "rate limit). Only call this when the user explicitly asks to CREATE/ADD/SET UP a new " +
                      "rule, and only when the environment+module matches exactly one entry in MONITORED MODULES " +
                      "and the pipeline matches exactly one entry in AVAILABLE BUILD PIPELINES. Never call if " +
                      "ambiguous. Do NOT call this for questions that just ASK ABOUT existing rules (e.g. " +
                      "\"list the rules\", \"what rules exist\", \"is there a rule for X\") — those are answered " +
                      "directly from the EXISTING REMEDIATION RULES data already provided, with no tool call.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                environment = new { type = "string", description = "Exact environment name from MONITORED MODULES." },
                module = new { type = "string", description = "Exact module name from MONITORED MODULES." },
                pipelineName = new { type = "string", description = "Exact pipeline name, copied verbatim from AVAILABLE BUILD PIPELINES." },
                branch = new { type = "string", description = "Branch to build, only if the user specified one." },
                maxActionsPerHour = new { type = "integer", description = "Rate limit; defaults to 1 per hour if the user didn't specify one." }
            },
            required = new[] { "environment", "module", "pipelineName" }
        }
    };

    private static readonly ClaudeToolDefinition UpdateAlertsTool = new()
    {
        Name = "update_alerts",
        Description = "Update alert delivery settings (enable/disable, Teams webhook, SMTP). Only call when the " +
                      "user clearly specified what to change. Omit fields the user didn't mention.",
        InputSchema = new
        {
            type = "object",
            properties = new
            {
                enabled = new { type = "boolean", description = "Whether alert delivery is enabled." },
                teamsWebhookUrl = new { type = "string", description = "Teams incoming webhook URL, only if the user gave one." },
                smtpHost = new { type = "string" },
                smtpPort = new { type = "integer" },
                smtpUsername = new { type = "string" },
                smtpFrom = new { type = "string" },
                smtpTo = new { type = "string" }
            },
            required = new[] { "enabled" }
        }
    };

    private static readonly ClaudeToolDefinition[] AllTools =
    {
        TriggerBuildTool, CancelBuildTool, DeletePipelineTool, RenamePipelineTool,
        CreateTargetTool, UpdateTargetTool, DeleteTargetTool,
        CreateUserTool, DeleteUserTool, ChangeUserRoleTool,
        UpdateAlertsTool, CreateRemediationRuleTool
    };

    // --- Tool-enabled ask + resolution --------------------------------------------------

    // Keyword buckets deciding which tools/catalogs are relevant to a given question — sending
    // all 11 tool schemas plus every catalog (156+ pipelines, all targets, all users) on every
    // request is large enough to exceed tighter free-tier provider budgets (e.g. Groq's 6K
    // TPM), and also gives the model less to get confused by. Falls back to everything if the
    // question doesn't clearly match a bucket, so nothing is ever silently unreachable.
    private static readonly string[] PipelineKeywords = { "pipeline", "build", "trigger", "queue", "cancel", "stop", "rename", "delete pipeline" };
    private static readonly string[] TargetKeywords = { "target", "monitor", "module", "environment", "api host", "route prefix" };
    private static readonly string[] UserKeywords = { "user", "account", "role", "admin", "developer", "login" };
    private static readonly string[] AlertKeywords = { "alert", "teams", "smtp", "webhook", "notification", "email" };
    private static readonly string[] RemediationKeywords = { "remediation", "remediate", "auto-fix", "auto fix", "self-heal", "self heal", "autopilot", "auto-remediat" };

    private async Task<ChatResponseDto> AskWithToolsAsync(string liveData, string question, CancellationToken ct)
    {
        bool Matches(string[] keywords) => keywords.Any(k => question.Contains(k, StringComparison.OrdinalIgnoreCase));

        var wantsPipeline = Matches(PipelineKeywords);
        var wantsTarget = Matches(TargetKeywords);
        var wantsUser = Matches(UserKeywords);
        var wantsAlerts = Matches(AlertKeywords);
        var wantsRemediation = Matches(RemediationKeywords);
        // NOTE: this used to fall back to "load every catalog + every tool" when no keyword
        // matched — meant for genuinely ambiguous admin requests, but it also fired for plain
        // small talk ("what r u doing"), ballooning a trivial question to ~7K+ tokens and
        // blowing free-tier per-minute token limits. An unmatched question was never going to
        // trigger a tool call anyway (the system prompt already requires an unambiguous keyword
        // match), so there's nothing lost by keeping it lightweight instead — every catalog/tool
        // below is now gated purely on its own keyword match.

        // Remediation-rule creation needs the pipeline catalog too (to validate pipelineName).
        var pipelineCatalog = (wantsPipeline || wantsRemediation) ? await _context.GetBuildPipelineCatalogAsync(ct) : new List<BuildPipelineDto>();
        var userCatalog = wantsUser ? await _context.GetUsersCatalogAsync(ct) : new List<AppUser>();

        // Longest-match wins — e.g. "QATEST" must not be matched by the shorter "QA" just
        // because "QATEST" contains "QA" as a substring. Shared by target and monitored-module
        // scoping below since both key off the same environment names.
        string? FindNamedEnvironment(IEnumerable<string> environmentNames) =>
            environmentNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(e => question.Contains(e, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .FirstOrDefault();

        // The merged target catalog can be 600+ rows (every module across every environment) —
        // large enough on its own to blow tighter free-tier budgets. If the question names a
        // specific environment, scope to just that one (a handful of rows); otherwise cap with
        // a note. The confirm endpoint always re-resolves against the live DB regardless, so a
        // capped/scoped list here only affects which targets the model can identify by name —
        // not correctness of the eventual mutation.
        var targetCatalog = new List<ImportedTarget>();
        var targetsTruncated = false;
        const int MaxTargetsListed = 60;
        if (wantsTarget)
        {
            var allTargets = await _context.GetTargetsCatalogAsync(ct);
            var namedEnv = FindNamedEnvironment(allTargets.Select(t => t.Environment));
            if (namedEnv is not null)
            {
                targetCatalog = allTargets.Where(t => string.Equals(t.Environment, namedEnv, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                targetCatalog = allTargets.Take(MaxTargetsListed).ToList();
                targetsTruncated = allTargets.Count > MaxTargetsListed;
            }
        }

        // Same oversized-catalog problem as targets above (150+ monitored modules across every
        // environment) — scope to a named environment when possible, otherwise cap with a note.
        var monitoredModulesCatalog = new List<(string Environment, string Module)>();
        var modulesTruncated = false;
        const int MaxModulesListed = 60;
        if (wantsRemediation)
        {
            var allModules = await _context.GetMonitoredModulesCatalogAsync(ct);
            var namedEnv = FindNamedEnvironment(allModules.Select(m => m.Environment));
            if (namedEnv is not null)
            {
                monitoredModulesCatalog = allModules.Where(m => string.Equals(m.Environment, namedEnv, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                monitoredModulesCatalog = allModules.Take(MaxModulesListed).ToList();
                modulesTruncated = allModules.Count > MaxModulesListed;
            }
        }

        // Read-only — no tool needed, just fed into the prompt so "what rules exist" can be
        // answered directly from LIVE DATA, same as pipelines/targets/users.
        var remediationRulesCatalog = wantsRemediation ? await _context.GetRemediationRulesCatalogAsync(ct) : new List<RemediationRule>();

        var tools = new List<ClaudeToolDefinition>();
        if (wantsPipeline) tools.AddRange(new[] { TriggerBuildTool, CancelBuildTool, DeletePipelineTool, RenamePipelineTool });
        if (wantsTarget) tools.AddRange(new[] { CreateTargetTool, UpdateTargetTool, DeleteTargetTool });
        if (wantsUser) tools.AddRange(new[] { CreateUserTool, DeleteUserTool, ChangeUserRoleTool });
        if (wantsAlerts) tools.Add(UpdateAlertsTool);
        if (wantsRemediation) tools.Add(CreateRemediationRuleTool);

        var promptSections = new List<string>();
        if (pipelineCatalog.Count > 0 || wantsPipeline || wantsRemediation)
        {
            promptSections.Add("AVAILABLE BUILD PIPELINES:\n" + (pipelineCatalog.Count == 0 ? "(none available)" : string.Join("\n", pipelineCatalog.Select(p => $"- {p.Name}"))));
        }
        if (monitoredModulesCatalog.Count > 0 || wantsRemediation)
        {
            var truncNote = modulesTruncated ? "\n(List truncated — mention a specific environment name to see its full module list.)" : "";
            promptSections.Add("MONITORED MODULES (environment / module):\n" +
                (monitoredModulesCatalog.Count == 0 ? "(none)" : string.Join("\n", monitoredModulesCatalog.Select(m => $"- {m.Environment} / {m.Module}"))) + truncNote);
        }
        if (remediationRulesCatalog.Count > 0 || wantsRemediation)
        {
            string PipelineLabel(int definitionId) =>
                pipelineCatalog.FirstOrDefault(p => p.Id == definitionId)?.Name ?? $"definition {definitionId}";
            promptSections.Add("EXISTING REMEDIATION RULES:\n" +
                (remediationRulesCatalog.Count == 0 ? "(none configured)" : string.Join("\n", remediationRulesCatalog.Select(r =>
                    $"- {r.Environment} / {r.Module} -> '{PipelineLabel(r.AzureDevOpsDefinitionId)}'" +
                    $"{(string.IsNullOrWhiteSpace(r.Branch) ? "" : $" (branch {r.Branch})")}, enabled={r.Enabled}, max {r.MaxActionsPerHour}/hour"))));
        }
        if (targetCatalog.Count > 0 || wantsTarget)
        {
            var truncNote = targetsTruncated ? "\n(List truncated — mention a specific environment name to see its full target list.)" : "";
            promptSections.Add("EXISTING TARGETS:\n" + (targetCatalog.Count == 0 ? "(none)" : string.Join("\n", targetCatalog.Select(t => $"- {t.Environment} / {t.Module} (host={t.ApiHost}, prefix={t.RoutePrefix})"))) + truncNote);
        }
        if (userCatalog.Count > 0 || wantsUser)
        {
            promptSections.Add("EXISTING USERS:\n" + (userCatalog.Count == 0 ? "(none)" : string.Join("\n", userCatalog.Select(u => $"- {u.Username} ({u.Role})"))));
        }

        var systemPrompt = "You are Jarvis, a monitoring assistant for a DevOps dashboard, talking to an admin. " +
                            "For questions about the user's systems or requests to trigger/cancel/delete/rename " +
                            "build pipelines, manage monitored targets, manage user accounts, or update alert " +
                            "settings — answer/act ONLY using the LIVE DATA and catalogs below, do not use general " +
                            "knowledge, do not speculate beyond the data. Only call a tool when the user's request " +
                            "is unambiguous and matches real data listed below exactly. If a match is ambiguous, or " +
                            "nothing matches, do not call any tool — ask a clarifying question in plain text " +
                            "instead. Never guess a name, id, or value that isn't in the lists below. A question " +
                            "that only ASKS ABOUT something (list/show/what/is there) never needs a tool call — " +
                            "answer it directly from the data sections below in plain text; tools are only for " +
                            "requests to CHANGE something.\n\n" +
                            "For general/meta questions not about the user's monitored systems — small talk, " +
                            "the current date/time, or \"what can you do\" — you may answer using the ABOUT JARVIS " +
                            "section and general knowledge below.\n\n" +
                            AboutJarvisSection + "\n\n" +
                            string.Join("\n\n", promptSections) + "\n\n" +
                            "LIVE DATA:\n" + liveData;

        var result = await _claude.CompleteWithToolsAsync(systemPrompt, question, tools, maxTokens: 500, ct: ct);
        if (result is null)
        {
            return await NotConfiguredResponseAsync(ct);
        }

        if (result.ToolName is not null && result.ToolInput is JsonElement input)
        {
            return ResolveToolCall(result.ToolName, input, pipelineCatalog, targetCatalog, userCatalog, monitoredModulesCatalog);
        }

        return new ChatResponseDto { Answer = (result.Text ?? "").Trim(), ClaudeConfigured = true };
    }

    private static ChatResponseDto ResolveToolCall(
        string toolName, JsonElement input,
        List<BuildPipelineDto> pipelineCatalog, List<ImportedTarget> targetCatalog, List<AppUser> userCatalog,
        List<(string Environment, string Module)> monitoredModulesCatalog)
    {
        string? Str(string field) => input.TryGetProperty(field, out var el) ? el.GetString() : null;
        int? Int(string field) => input.TryGetProperty(field, out var el) && el.TryGetInt32(out var v) ? v : null;
        bool? Bool(string field) => input.TryGetProperty(field, out var el) ? el.GetBoolean() : (bool?)null;

        BuildPipelineDto? FindPipeline(string? name) =>
            pipelineCatalog.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        ImportedTarget? FindTarget(string? environment, string? module) =>
            targetCatalog.FirstOrDefault(t =>
                string.Equals(t.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Module, module, StringComparison.OrdinalIgnoreCase));

        AppUser? FindUser(string? username) =>
            userCatalog.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        ChatResponseDto Pending(string type, string answer, Dictionary<string, object?> parms) => new()
        {
            ClaudeConfigured = true,
            Answer = answer,
            PendingAction = new PendingActionDto
            {
                Type = type,
                Params = parms.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value))
            }
        };

        ChatResponseDto NotFound(string what) => new() { ClaudeConfigured = true, Answer = $"I couldn't find {what} — please check the name and try again." };

        switch (toolName)
        {
            case "trigger_build":
            {
                var name = Str("pipelineName");
                var pipeline = FindPipeline(name);
                if (pipeline is null) return NotFound($"a pipeline named '{name}'");
                var branch = Str("branch");
                var branchNote = string.IsNullOrWhiteSpace(branch) ? "its default branch" : $"branch '{branch}'";
                return Pending("trigger_build", $"Ready to trigger a build for '{pipeline.Name}' on {branchNote}. Confirm to proceed.",
                    new() { ["definitionId"] = pipeline.Id, ["name"] = pipeline.Name, ["branch"] = branch });
            }
            case "cancel_build":
            {
                var name = Str("pipelineName");
                var pipeline = FindPipeline(name);
                if (pipeline is null) return NotFound($"a pipeline named '{name}'");
                if (pipeline.LatestBuildId is not int buildId) return new ChatResponseDto { ClaudeConfigured = true, Answer = $"'{pipeline.Name}' has no recent build to cancel." };
                return Pending("cancel_build", $"Ready to cancel the latest build (#{pipeline.LatestBuildNumber}) of '{pipeline.Name}'. Confirm to proceed.",
                    new() { ["buildId"] = buildId, ["name"] = pipeline.Name });
            }
            case "delete_pipeline":
            {
                var name = Str("pipelineName");
                var pipeline = FindPipeline(name);
                if (pipeline is null) return NotFound($"a pipeline named '{name}'");
                return Pending("delete_pipeline", $"⚠️ Ready to PERMANENTLY DELETE pipeline definition '{pipeline.Name}'. This cannot be undone. Confirm to proceed.",
                    new() { ["definitionId"] = pipeline.Id, ["name"] = pipeline.Name });
            }
            case "rename_pipeline":
            {
                var name = Str("pipelineName");
                var newName = Str("newName");
                var pipeline = FindPipeline(name);
                if (pipeline is null) return NotFound($"a pipeline named '{name}'");
                if (string.IsNullOrWhiteSpace(newName)) return new ChatResponseDto { ClaudeConfigured = true, Answer = "A new name is required to rename a pipeline." };
                return Pending("rename_pipeline", $"Ready to rename pipeline '{pipeline.Name}' to '{newName}'. Confirm to proceed.",
                    new() { ["definitionId"] = pipeline.Id, ["oldName"] = pipeline.Name, ["newName"] = newName });
            }
            case "create_target":
            {
                var environment = Str("environment");
                var module = Str("module");
                var apiHost = Str("apiHost");
                var routePrefix = Str("routePrefix");
                if (string.IsNullOrWhiteSpace(environment) || string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(apiHost) || string.IsNullOrWhiteSpace(routePrefix))
                {
                    return new ChatResponseDto { ClaudeConfigured = true, Answer = "Environment, module, API host, and route prefix are all required to create a target." };
                }
                if (FindTarget(environment, module) is not null)
                {
                    return new ChatResponseDto { ClaudeConfigured = true, Answer = $"A target for {environment} / {module} already exists — did you mean to update it instead?" };
                }
                return Pending("create_target", $"Ready to add a new target: {environment} / {module} (host={apiHost}, prefix={routePrefix}). Confirm to proceed.",
                    new() { ["environment"] = environment, ["module"] = module, ["apiHost"] = apiHost, ["routePrefix"] = routePrefix });
            }
            case "update_target":
            {
                var environment = Str("environment");
                var module = Str("module");
                var target = FindTarget(environment, module);
                if (target is null) return NotFound($"an existing target for {environment} / {module}");
                var apiHost = Str("apiHost") ?? target.ApiHost;
                var routePrefix = Str("routePrefix") ?? target.RoutePrefix;
                return Pending("update_target", $"Ready to update {target.Environment} / {target.Module}: host={apiHost}, prefix={routePrefix}. Confirm to proceed.",
                    new() { ["targetId"] = target.Id, ["environment"] = target.Environment, ["module"] = target.Module, ["apiHost"] = apiHost, ["routePrefix"] = routePrefix });
            }
            case "delete_target":
            {
                var environment = Str("environment");
                var module = Str("module");
                var target = FindTarget(environment, module);
                if (target is null) return NotFound($"an existing target for {environment} / {module}");
                return Pending("delete_target", $"Ready to remove the target {target.Environment} / {target.Module}. Confirm to proceed.",
                    new() { ["targetId"] = target.Id, ["environment"] = target.Environment, ["module"] = target.Module });
            }
            case "create_user":
            {
                var username = Str("username");
                var role = Str("role");
                if (string.IsNullOrWhiteSpace(username) || !Roles.IsValid(role))
                {
                    return new ChatResponseDto { ClaudeConfigured = true, Answer = "A username and a valid role ('admin' or 'developer') are required." };
                }
                if (FindUser(username) is not null)
                {
                    return new ChatResponseDto { ClaudeConfigured = true, Answer = $"A user named '{username}' already exists." };
                }
                return Pending("create_user", $"Ready to create user '{username}' with role '{role}'. A random password will be generated and shown once you confirm. Confirm to proceed.",
                    new() { ["username"] = username, ["role"] = role });
            }
            case "delete_user":
            {
                var username = Str("username");
                var user = FindUser(username);
                if (user is null) return NotFound($"a user named '{username}'");
                return Pending("delete_user", $"⚠️ Ready to DELETE user '{user.Username}'. Confirm to proceed.",
                    new() { ["userId"] = user.Id, ["username"] = user.Username });
            }
            case "change_user_role":
            {
                var username = Str("username");
                var newRole = Str("newRole");
                var user = FindUser(username);
                if (user is null) return NotFound($"a user named '{username}'");
                if (!Roles.IsValid(newRole))
                {
                    return new ChatResponseDto { ClaudeConfigured = true, Answer = "Role must be 'admin' or 'developer'." };
                }
                return Pending("change_user_role", $"Ready to change '{user.Username}' role from {user.Role} to {newRole}. Confirm to proceed.",
                    new() { ["userId"] = user.Id, ["username"] = user.Username, ["newRole"] = newRole });
            }
            case "update_alerts":
            {
                var enabled = Bool("enabled") ?? false;
                return Pending("update_alerts", $"Ready to update alert settings (enabled={enabled}). Any Teams/SMTP fields you specified will also be applied. Confirm to proceed.",
                    new()
                    {
                        ["enabled"] = enabled,
                        ["teamsWebhookUrl"] = Str("teamsWebhookUrl"),
                        ["smtpHost"] = Str("smtpHost"),
                        ["smtpPort"] = Int("smtpPort"),
                        ["smtpUsername"] = Str("smtpUsername"),
                        ["smtpFrom"] = Str("smtpFrom"),
                        ["smtpTo"] = Str("smtpTo")
                    });
            }
            case "create_remediation_rule":
            {
                var environment = Str("environment");
                var module = Str("module");
                var pipelineName = Str("pipelineName");
                var isMonitored = monitoredModulesCatalog.Any(m =>
                    string.Equals(m.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.Module, module, StringComparison.OrdinalIgnoreCase));
                if (!isMonitored) return NotFound($"a monitored module '{module}' in '{environment}'");
                var pipeline = FindPipeline(pipelineName);
                if (pipeline is null) return NotFound($"a pipeline named '{pipelineName}'");
                var branch = Str("branch");
                var maxPerHour = Int("maxActionsPerHour") ?? 1;
                return Pending("create_remediation_rule",
                    $"Ready to create an auto-remediation rule: if '{module}' in '{environment}' goes down, " +
                    $"automatically retrigger '{pipeline.Name}'{(string.IsNullOrWhiteSpace(branch) ? "" : $" on branch '{branch}'")} " +
                    $"(max {maxPerHour}/hour). This only takes effect once auto-remediation is enabled in Settings. Confirm to proceed.",
                    new()
                    {
                        ["environment"] = environment,
                        ["module"] = module,
                        ["definitionId"] = pipeline.Id,
                        ["pipelineName"] = pipeline.Name,
                        ["branch"] = branch,
                        ["maxActionsPerHour"] = maxPerHour
                    });
            }
            default:
                return new ChatResponseDto { ClaudeConfigured = true, Answer = "I identified an action I don't know how to perform. Please try rephrasing." };
        }
    }

    // --- Confirm: the only place any mutation actually happens --------------------------

    [HttpPost("confirm")]
    public async Task<ActionResult<ChatConfirmResponseDto>> Confirm([FromBody] ChatConfirmRequest req, CancellationToken ct)
    {
        if (!CurrentUser.IsAdmin(User))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin role required." });
        }

        string? Str(string field) => req.Params.TryGetValue(field, out var el) && el.ValueKind != JsonValueKind.Null ? el.GetString() : null;
        int? Int(string field) => req.Params.TryGetValue(field, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
        bool Bool(string field) => req.Params.TryGetValue(field, out var el) && el.ValueKind == JsonValueKind.True;

        var ip = BasicAuthMiddleware.ClientIp(HttpContext);
        var actorId = CurrentUser.Id(User);
        var actorName = CurrentUser.Name(User);

        switch (req.Type)
        {
            case "trigger_build":
            {
                var definitionId = Int("definitionId") ?? 0;
                var name = Str("name") ?? $"#{definitionId}";
                var branch = Str("branch");
                var result = await _azure.QueueBuildAsync(definitionId, branch, ct);
                var detail = result.Ok
                    ? $"Queued build #{result.BuildId} for '{name}'{(string.IsNullOrWhiteSpace(branch) ? "" : $" on branch '{branch}'")} (via Ask Jarvis)"
                    : $"Failed to queue build for '{name}' (via Ask Jarvis): {result.Error}";
                await _activity.LogAsync(actorId, actorName, "pipeline.trigger", detail, ip);
                return Ok(new ChatConfirmResponseDto { Ok = result.Ok, Message = result.Ok ? $"Queued build #{result.BuildId} for '{name}'." : $"Failed: {result.Error}" });
            }
            case "cancel_build":
            {
                var buildId = Int("buildId") ?? 0;
                var name = Str("name") ?? $"#{buildId}";
                var result = await _azure.CancelBuildAsync(buildId, ct);
                var detail = result.Ok
                    ? $"Requested cancellation of build #{buildId} for '{name}' (via Ask Jarvis)"
                    : $"Failed to cancel build #{buildId} for '{name}' (via Ask Jarvis): {result.Error}";
                await _activity.LogAsync(actorId, actorName, "pipeline.cancel", detail, ip);
                return Ok(new ChatConfirmResponseDto { Ok = result.Ok, Message = result.Ok ? $"Cancellation requested for build #{buildId}." : $"Failed: {result.Error}" });
            }
            case "delete_pipeline":
            {
                var definitionId = Int("definitionId") ?? 0;
                var name = Str("name") ?? $"#{definitionId}";
                var result = await _azure.DeleteBuildDefinitionAsync(definitionId, name, ct);
                var detail = result.Ok
                    ? $"Deleted pipeline definition '{name}' (#{definitionId}) (via Ask Jarvis)"
                    : $"Failed to delete pipeline definition '{name}' (via Ask Jarvis): {result.Error}";
                await _activity.LogAsync(actorId, actorName, "pipeline.delete", detail, ip);
                return Ok(new ChatConfirmResponseDto { Ok = result.Ok, Message = result.Ok ? $"Deleted pipeline '{name}'." : $"Failed: {result.Error}" });
            }
            case "rename_pipeline":
            {
                var definitionId = Int("definitionId") ?? 0;
                var oldName = Str("oldName") ?? $"#{definitionId}";
                var newName = Str("newName") ?? oldName;
                var result = await _azure.RenameBuildDefinitionAsync(definitionId, oldName, newName, ct);
                var detail = result.Ok
                    ? $"Renamed pipeline definition #{definitionId} from '{oldName}' to '{newName}' (via Ask Jarvis)"
                    : $"Failed to rename pipeline definition #{definitionId} (via Ask Jarvis): {result.Error}";
                await _activity.LogAsync(actorId, actorName, "pipeline.rename", detail, ip);
                return Ok(new ChatConfirmResponseDto { Ok = result.Ok, Message = result.Ok ? $"Renamed to '{newName}'." : $"Failed: {result.Error}" });
            }
            case "create_target":
            {
                var environment = Str("environment") ?? "";
                var module = Str("module") ?? "";
                if (await _db.ImportedTargets.AnyAsync(t => t.Environment == environment && t.Module == module, ct))
                {
                    return Ok(new ChatConfirmResponseDto { Ok = false, Message = "A target for that environment/module already exists." });
                }
                var target = new ImportedTarget
                {
                    Environment = environment.Trim(),
                    Module = module.Trim(),
                    ApiHost = (Str("apiHost") ?? "").Trim(),
                    RoutePrefix = (Str("routePrefix") ?? "").Trim(),
                    Origin = "manual",
                    SourceFile = "",
                    CreatedAtUtc = DateTime.UtcNow
                };
                _db.ImportedTargets.Add(target);
                await _db.SaveChangesAsync(ct);
                await _activity.LogAsync(actorId, actorName, "target.create", $"Created target {environment} / {module} (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto { Ok = true, Message = $"Created target {environment} / {module}." });
            }
            case "update_target":
            {
                var targetId = Int("targetId") ?? 0;
                var target = await _db.ImportedTargets.FirstOrDefaultAsync(t => t.Id == targetId, ct);
                if (target is null) return Ok(new ChatConfirmResponseDto { Ok = false, Message = "That target no longer exists." });
                target.ApiHost = (Str("apiHost") ?? target.ApiHost).Trim();
                target.RoutePrefix = (Str("routePrefix") ?? target.RoutePrefix).Trim();
                await _db.SaveChangesAsync(ct);
                await _activity.LogAsync(actorId, actorName, "target.update", $"Updated target {target.Environment} / {target.Module} (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto { Ok = true, Message = $"Updated {target.Environment} / {target.Module}." });
            }
            case "delete_target":
            {
                var targetId = Int("targetId") ?? 0;
                var target = await _db.ImportedTargets.FirstOrDefaultAsync(t => t.Id == targetId, ct);
                if (target is null) return Ok(new ChatConfirmResponseDto { Ok = false, Message = "That target no longer exists." });
                _db.ImportedTargets.Remove(target);
                await _db.SaveChangesAsync(ct);
                await _activity.LogAsync(actorId, actorName, "target.delete", $"Deleted target {target.Environment} / {target.Module} (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto { Ok = true, Message = $"Removed {target.Environment} / {target.Module}." });
            }
            case "create_user":
            {
                var username = Str("username") ?? "";
                var role = Str("role") ?? Roles.Developer;
                if (await _db.AppUsers.AnyAsync(u => u.Username == username, ct))
                {
                    return Ok(new ChatConfirmResponseDto { Ok = false, Message = "That username is already taken." });
                }
                var password = GenerateTemporaryPassword();
                var user = new AppUser { Username = username, Role = role, CreatedAtUtc = DateTime.UtcNow };
                user.PasswordHash = _hasher.HashPassword(user, password);
                _db.AppUsers.Add(user);
                await _db.SaveChangesAsync(ct);
                await _activity.LogAsync(actorId, actorName, "user.create", $"Created user '{username}' ({role}) (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto { Ok = true, Message = $"Created user '{username}' ({role}). Temporary password: {password} — share this securely and have them change it." });
            }
            case "delete_user":
            {
                var userId = Int("userId") ?? 0;
                if (userId == actorId)
                {
                    return Ok(new ChatConfirmResponseDto { Ok = false, Message = "You cannot delete your own account." });
                }
                var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);
                if (user is null) return Ok(new ChatConfirmResponseDto { Ok = false, Message = "That user no longer exists." });
                if (user.Role == Roles.Admin && await _db.AppUsers.CountAsync(u => u.Role == Roles.Admin, ct) <= 1)
                {
                    return Ok(new ChatConfirmResponseDto { Ok = false, Message = "Cannot delete the last admin account." });
                }
                _db.AppUsers.Remove(user);
                await _db.SaveChangesAsync(ct);
                await _activity.LogAsync(actorId, actorName, "user.delete", $"Deleted user '{user.Username}' (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto { Ok = true, Message = $"Deleted user '{user.Username}'." });
            }
            case "change_user_role":
            {
                var userId = Int("userId") ?? 0;
                var newRole = Str("newRole") ?? Roles.Developer;
                var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);
                if (user is null) return Ok(new ChatConfirmResponseDto { Ok = false, Message = "That user no longer exists." });
                if (user.Role == Roles.Admin && newRole != Roles.Admin
                    && await _db.AppUsers.CountAsync(u => u.Role == Roles.Admin, ct) <= 1)
                {
                    return Ok(new ChatConfirmResponseDto { Ok = false, Message = "Cannot demote the last admin account." });
                }
                var oldRole = user.Role;
                user.Role = newRole;
                await _db.SaveChangesAsync(ct);
                await _activity.LogAsync(actorId, actorName, "user.role-change", $"Changed '{user.Username}' role from {oldRole} to {newRole} (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto { Ok = true, Message = $"Changed '{user.Username}' role to {newRole}." });
            }
            case "update_alerts":
            {
                var enabled = Bool("enabled");
                await _settings.SetAsync(SettingKeys.AlertsEnabled, enabled ? "true" : "false", ct);
                if (Str("smtpHost") is string smtpHost) await _settings.SetAsync(SettingKeys.AlertsSmtpHost, smtpHost.Trim(), ct);
                if (Int("smtpPort") is int smtpPort) await _settings.SetAsync(SettingKeys.AlertsSmtpPort, smtpPort.ToString(), ct);
                if (Str("smtpUsername") is string smtpUsername) await _settings.SetAsync(SettingKeys.AlertsSmtpUsername, smtpUsername.Trim(), ct);
                if (Str("smtpFrom") is string smtpFrom) await _settings.SetAsync(SettingKeys.AlertsSmtpFrom, smtpFrom.Trim(), ct);
                if (Str("smtpTo") is string smtpTo) await _settings.SetAsync(SettingKeys.AlertsSmtpTo, smtpTo.Trim(), ct);
                if (Str("teamsWebhookUrl") is string teamsUrl && !string.IsNullOrWhiteSpace(teamsUrl))
                {
                    await _settings.SetAsync(SettingKeys.AlertsTeamsWebhookUrl, teamsUrl.Trim(), ct);
                }
                await _activity.LogAsync(actorId, actorName, "settings.alerts", $"Updated alert settings (enabled={enabled}) (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto { Ok = true, Message = "Alert settings updated." });
            }
            case "create_remediation_rule":
            {
                var environment = Str("environment") ?? "";
                var module = Str("module") ?? "";
                var definitionId = Int("definitionId") ?? 0;
                var pipelineName = Str("pipelineName") ?? $"#{definitionId}";
                var branch = Str("branch");
                var maxPerHour = Int("maxActionsPerHour") ?? 1;

                var rule = await _db.RemediationRules.FirstOrDefaultAsync(
                    r => r.Environment == environment && r.Module == module, ct);
                if (rule is null)
                {
                    rule = new RemediationRule { Environment = environment, Module = module };
                    _db.RemediationRules.Add(rule);
                }
                rule.AzureDevOpsDefinitionId = definitionId;
                rule.Branch = branch;
                rule.Enabled = true;
                rule.MaxActionsPerHour = maxPerHour <= 0 ? 1 : maxPerHour;
                rule.UpdatedAtUtc = DateTime.UtcNow;
                rule.UpdatedBy = actorName;
                await _db.SaveChangesAsync(ct);

                await _activity.LogAsync(actorId, actorName, "settings.remediation-rule",
                    $"Created remediation rule {environment}/{module} -> '{pipelineName}' (via Ask Jarvis)", ip);
                return Ok(new ChatConfirmResponseDto
                {
                    Ok = true,
                    Message = $"Created remediation rule: {environment}/{module} -> '{pipelineName}' (max {rule.MaxActionsPerHour}/hour). " +
                              "Remember auto-remediation must be enabled in Settings for this to take effect."
                });
            }
            default:
                return BadRequest(new { error = "Unknown action type." });
        }
    }

    private static string GenerateTemporaryPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        var bytes = RandomNumberGenerator.GetBytes(16);
        var result = new char[16];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = chars[bytes[i] % chars.Length];
        }
        return new string(result);
    }

    // Reports the actually-active provider (not always "Claude") so a rate limit or bad key on
    // Gemini/Groq doesn't get misattributed to Claude — IClaudeService's CompleteAsync/
    // CompleteWithToolsAsync return null for any failure (missing key, HTTP error, rate limit),
    // so this can't name the exact cause, only point at Settings/logs for it.
    private async Task<ChatResponseDto> NotConfiguredResponseAsync(CancellationToken ct)
    {
        var provider = await _settings.GetEffectiveAsync(Models.SettingKeys.AiProvider, ct);
        var providerName = provider?.ToLowerInvariant() switch
        {
            "gemini" => "Gemini",
            "groq" => "Groq",
            _ => "Claude"
        };
        return new ChatResponseDto
        {
            Answer = $"The {providerName} AI assistant isn't responding right now — it may be unconfigured, " +
                     "rate-limited, or the request failed. Check Settings, or the server logs for the exact cause.",
            ClaudeConfigured = false
        };
    }

}
