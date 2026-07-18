using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Monitoring_API.Models;

namespace Monitoring_API.Services;

// Azure DevOps pipeline dashboard (ported from Sentinel's azure-devops.js).
// Talks to the Build API (dev.azure.com) and Release API (vsrm.dev.azure.com)
// with a PAT, and merges build + release pipelines into one dashboard payload.
public class AzureDevOpsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<AzureDevOpsService> _logger;

    // Resolved per-request from the settings store (DB value overrides env var).
    private string? _org;
    private string? _project;
    private string? _pat;
    private string _apiVersion = "7.1";

    public AzureDevOpsService(IHttpClientFactory httpClientFactory, SettingsService settings, ILogger<AzureDevOpsService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    private bool _configLoaded;

    // Idempotent: only hits the DB once per service-instance lifetime. This
    // matters beyond just avoiding redundant queries — GetInsights() fans out
    // many GetBuildHistoryAsync/GetReleaseHistoryAsync calls in parallel over
    // this same (scoped) instance, and EF Core's DbContext is not safe for
    // concurrent use. Loading config once, synchronously, before any parallel
    // fan-out — which GetPipelineDashboardAsync already does first in that
    // flow — means the parallel calls never touch the DB at all.
    private async Task LoadConfigAsync(CancellationToken ct)
    {
        if (_configLoaded) return;
        _org = SanitizeOrg(await _settings.GetEffectiveAsync(SettingKeys.AzureOrg, ct));
        _project = await _settings.GetEffectiveAsync(SettingKeys.AzureProject, ct);
        _project = string.IsNullOrWhiteSpace(_project) ? _project : _project!.Trim();
        _pat = await _settings.GetEffectiveAsync(SettingKeys.AzurePat, ct);
        var ver = await _settings.GetEffectiveAsync(SettingKeys.AzureApiVersion, ct);
        _apiVersion = string.IsNullOrWhiteSpace(ver) ? "7.1" : ver!.Trim();
        _configLoaded = true;
    }

    // Accept a bare org name OR a pasted URL like "https://dev.azure.com/myorg/"
    // and reduce it to just "myorg" so the base URL isn't doubled up.
    private static string? SanitizeOrg(string? org)
    {
        if (string.IsNullOrWhiteSpace(org)) return org;
        org = org.Trim();
        if (org.Contains("://", StringComparison.Ordinal))
        {
            var segs = org.Split('/', StringSplitOptions.RemoveEmptyEntries);
            // scheme(0) host(1) org(2...) — take the first path segment after the host.
            org = segs.Length >= 3 ? segs[2] : org;
        }
        return org.Trim('/');
    }

    // Project must be URL-encoded (names can contain spaces).
    private string EncodedProject => Uri.EscapeDataString(_project ?? string.Empty);

    private bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_org) && !string.IsNullOrWhiteSpace(_project) && !string.IsNullOrWhiteSpace(_pat);

    private HttpClient BuildClient() => Client($"https://dev.azure.com/{_org}/");
    private HttpClient ReleaseClient() => Client($"https://vsrm.dev.azure.com/{_org}/");

    private HttpClient Client(string baseUrl)
    {
        var client = _httpClientFactory.CreateClient("azure");
        client.BaseAddress = new Uri(baseUrl);
        // PAT is sent as basic auth with an empty username.
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{_pat}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        return client;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await client.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // 203/302 to a sign-in page or a 401/404 usually means wrong org/project or a
            // bad/expired PAT (or one missing Build/Release read scope).
            var hint = (int)resp.StatusCode switch
            {
                401 or 203 => "authentication failed — check the PAT (and that it has Build/Release read scope).",
                404 => "not found — check the organization and project names.",
                _ => "request rejected by Azure DevOps."
            };
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {hint}");
        }

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            // A 200 that returns HTML is Azure's sign-in page — almost always a PAT problem.
            throw new InvalidOperationException(
                "Received an HTML page instead of JSON — the PAT is likely invalid/expired or lacks scope, or the organization name is wrong.");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    // Same error-handling contract as GetJsonAsync, for write calls (queuing a build).
    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var hint = (int)resp.StatusCode switch
            {
                401 or 203 => "authentication failed — check the PAT (and that it has Build: Read & execute scope).",
                403 => "forbidden — the PAT likely lacks Build: Read & execute scope.",
                404 => "not found — check the build definition id, organization, and project.",
                _ => "request rejected by Azure DevOps."
            };
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {hint}");
        }

        var trimmed = respBody.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Received an HTML page instead of JSON — the PAT is likely invalid/expired or lacks scope.");
        }

        using var doc = JsonDocument.Parse(respBody);
        return doc.RootElement.Clone();
    }

    private static IEnumerable<JsonElement> Values(JsonElement root) =>
        root.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;

    private static DateTime? Date(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String && p.TryGetDateTime(out var dt) ? dt : null;

    private static string? StripRefs(string? branch) =>
        branch?.Replace("refs/heads/", "");

    // Accepts either a bare branch name ("develop") or an already-qualified
    // ref ("refs/heads/develop") and normalizes to the ref form Azure expects.
    private static string NormalizeBranch(string branch)
    {
        branch = branch.Trim();
        return branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";
    }

    public async Task<PipelineDashboardDto> GetPipelineDashboardAsync(CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured)
        {
            return new PipelineDashboardDto { Ok = false, Msg = "Azure DevOps credentials not configured." };
        }

        try
        {
            var build = BuildClient();
            var release = ReleaseClient();

            var buildDefsTask = GetJsonAsync(build, $"{EncodedProject}/_apis/build/definitions?api-version={_apiVersion}&$top=100", ct);
            var buildsTask = GetJsonAsync(build, $"{EncodedProject}/_apis/build/builds?$top=100&queryOrder=finishTimeDescending&api-version={_apiVersion}", ct);
            var releaseDefsTask = GetJsonAsync(release, $"{EncodedProject}/_apis/release/definitions?api-version={_apiVersion}&$top=100", ct);
            var releasesTask = GetJsonAsync(release, $"{EncodedProject}/_apis/release/releases?$top=15&queryOrder=descending&api-version={_apiVersion}", ct);

            await Task.WhenAll(buildDefsTask, buildsTask, releaseDefsTask, releasesTask);

            var buildDefs = Values(buildDefsTask.Result).ToList();
            var allBuilds = Values(buildsTask.Result).ToList();
            var releaseDefs = Values(releaseDefsTask.Result).ToList();
            var latestReleases = Values(releasesTask.Result).ToList();

            // Group builds by definition id (max 5 each), preserving finish-time order.
            var buildsByDef = new Dictionary<int, List<JsonElement>>();
            foreach (var b in allBuilds)
            {
                var defId = b.TryGetProperty("definition", out var d) ? Int(d, "id") : null;
                if (defId is null) continue;
                var list = buildsByDef.TryGetValue(defId.Value, out var l) ? l : buildsByDef[defId.Value] = new();
                if (list.Count < 5) list.Add(b);
            }

            var buildPipelines = buildDefs.Select(def =>
            {
                var id = Int(def, "id") ?? 0;
                var builds = buildsByDef.TryGetValue(id, out var l) ? l : new List<JsonElement>();
                var latest = builds.Count > 0 ? builds[0] : (JsonElement?)null;

                return new BuildPipelineDto
                {
                    Id = id,
                    Name = Str(def, "name") ?? "",
                    Path = Str(def, "path") ?? "\\",
                    QueueStatus = Str(def, "queueStatus"),
                    LatestStatus = latest is null ? "none" : (Str(latest.Value, "result") ?? Str(latest.Value, "status") ?? "none"),
                    LatestBuildId = latest is null ? null : Int(latest.Value, "id"),
                    LatestBuildNumber = latest is null ? null : Str(latest.Value, "buildNumber"),
                    LatestFinished = latest is null ? null : Date(latest.Value, "finishTime"),
                    LatestBranch = latest is null ? null : StripRefs(Str(latest.Value, "sourceBranch")),
                    LatestRequestedBy = latest is null ? null : (latest.Value.TryGetProperty("requestedFor", out var rf) ? Str(rf, "displayName") : null),
                    RecentBuilds = builds.Take(5).Select(b => new BuildSummaryDto
                    {
                        Id = Int(b, "id") ?? 0,
                        Number = Str(b, "buildNumber"),
                        Result = Str(b, "result") ?? Str(b, "status"),
                        FinishTime = Date(b, "finishTime"),
                        Branch = StripRefs(Str(b, "sourceBranch")),
                        RequestedBy = b.TryGetProperty("requestedFor", out var rf2) ? Str(rf2, "displayName") : null,
                        Url = $"https://dev.azure.com/{_org}/{EncodedProject}/_build/results?buildId={Int(b, "id")}"
                    }).ToList()
                };
            }).ToList();

            // Group releases by definition id (max 3 each).
            var releasesByDef = new Dictionary<int, List<JsonElement>>();
            foreach (var r in latestReleases)
            {
                var defId = r.TryGetProperty("releaseDefinition", out var d) ? Int(d, "id") : null;
                if (defId is null) continue;
                var list = releasesByDef.TryGetValue(defId.Value, out var l) ? l : releasesByDef[defId.Value] = new();
                if (list.Count < 3) list.Add(r);
            }

            var releasePipelines = releaseDefs.Select(def =>
            {
                var id = Int(def, "id") ?? 0;
                var releases = releasesByDef.TryGetValue(id, out var l) ? l : new List<JsonElement>();
                var latest = releases.Count > 0 ? releases[0] : (JsonElement?)null;

                var latestEnvs = latest is not null && latest.Value.TryGetProperty("environments", out var le) && le.ValueKind == JsonValueKind.Array
                    ? le.EnumerateArray().Select(e => new ReleaseEnvDto { Name = Str(e, "name") ?? "", Status = Str(e, "status") }).ToList()
                    : new List<ReleaseEnvDto>();

                var statuses = latestEnvs.Select(e => e.Status).ToList();
                string overall = "none";
                if (statuses.Contains("rejected") || statuses.Contains("failed")) overall = "failed";
                else if (statuses.Contains("inProgress")) overall = "inProgress";
                else if (statuses.Count > 0 && statuses.All(s => s == "succeeded")) overall = "succeeded";
                else if (statuses.Contains("succeeded")) overall = "partiallySucceeded";

                return new ReleasePipelineDto
                {
                    Id = id,
                    Name = Str(def, "name") ?? "",
                    Path = Str(def, "path") ?? "\\",
                    Environments = def.TryGetProperty("environments", out var de) && de.ValueKind == JsonValueKind.Array
                        ? de.EnumerateArray().Select(e => Str(e, "name") ?? "").ToList()
                        : new List<string>(),
                    LatestStatus = overall,
                    LatestReleaseName = latest is null ? null : Str(latest.Value, "name"),
                    LatestFinished = latest is null ? null : Date(latest.Value, "modifiedOn"),
                    LatestEnvironments = latestEnvs,
                    RecentReleases = releases.Select(r => new ReleaseSummaryDto
                    {
                        Id = Int(r, "id") ?? 0,
                        Name = Str(r, "name"),
                        Status = Str(r, "status"),
                        CreatedOn = Date(r, "createdOn"),
                        Environments = r.TryGetProperty("environments", out var re) && re.ValueKind == JsonValueKind.Array
                            ? re.EnumerateArray().Select(e => new ReleaseEnvDto { Name = Str(e, "name") ?? "", Status = Str(e, "status") }).ToList()
                            : new List<ReleaseEnvDto>(),
                        Url = $"https://dev.azure.com/{_org}/{EncodedProject}/_releaseProgress?releaseId={Int(r, "id")}"
                    }).ToList()
                };
            }).ToList();

            return new PipelineDashboardDto { Ok = true, BuildPipelines = buildPipelines, ReleasePipelines = releasePipelines };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure DevOps dashboard fetch failed");
            return new PipelineDashboardDto { Ok = false, Msg = ex.Message };
        }
    }

    // Deeper per-definition build history for insight computation (flakiness,
    // duration anomaly, trend) — separate from the main dashboard call so
    // that stays fast/unchanged. Azure already retains this history; we just
    // ask for more of it, scoped to one definition.
    public async Task<List<BuildHistoryItem>> GetBuildHistoryAsync(int definitionId, int days, CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured) return new();
        try
        {
            var since = DateTime.UtcNow.AddDays(-days).ToString("o");
            var root = await GetJsonAsync(BuildClient(),
                $"{EncodedProject}/_apis/build/builds?definitions={definitionId}&minTime={Uri.EscapeDataString(since)}" +
                $"&$top=50&queryOrder=finishTimeDescending&api-version={_apiVersion}", ct);

            return Values(root)
                .Select(b => new BuildHistoryItem
                {
                    Id = Int(b, "id") ?? 0,
                    Result = Str(b, "result") ?? Str(b, "status"),
                    StartTime = Date(b, "startTime"),
                    FinishTime = Date(b, "finishTime")
                })
                .Where(b => b.FinishTime is not null)
                .OrderBy(b => b.FinishTime)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps build history fetch failed for definition {DefinitionId}", definitionId);
            return new();
        }
    }

    public async Task<List<ReleaseHistoryItem>> GetReleaseHistoryAsync(int definitionId, int days, CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured) return new();
        try
        {
            var since = DateTime.UtcNow.AddDays(-days).ToString("o");
            var root = await GetJsonAsync(ReleaseClient(),
                $"{EncodedProject}/_apis/release/releases?definitionId={definitionId}&minCreatedTime={Uri.EscapeDataString(since)}" +
                $"&$top=50&queryOrder=descending&api-version={_apiVersion}", ct);

            return Values(root)
                .Select(r =>
                {
                    var envStatuses = r.TryGetProperty("environments", out var envs) && envs.ValueKind == JsonValueKind.Array
                        ? envs.EnumerateArray().Select(e => Str(e, "status")).ToList()
                        : new List<string?>();

                    string overall = "none";
                    if (envStatuses.Contains("rejected") || envStatuses.Contains("failed")) overall = "failed";
                    else if (envStatuses.Contains("inProgress")) overall = "inProgress";
                    else if (envStatuses.Count > 0 && envStatuses.All(s => s == "succeeded")) overall = "succeeded";
                    else if (envStatuses.Contains("succeeded")) overall = "partiallySucceeded";

                    return new ReleaseHistoryItem
                    {
                        Id = Int(r, "id") ?? 0,
                        OverallStatus = overall,
                        CreatedOn = Date(r, "createdOn"),
                        ModifiedOn = Date(r, "modifiedOn")
                    };
                })
                .Where(r => r.CreatedOn is not null && r.OverallStatus != "inProgress" && r.OverallStatus != "none")
                .OrderBy(r => r.CreatedOn)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps release history fetch failed for definition {DefinitionId}", definitionId);
            return new();
        }
    }

    // Queues a new build for a definition — the one write operation this service performs.
    // Build pipelines only (compile/test); release/deployment triggering is intentionally
    // not exposed here.
    public async Task<TriggeredBuildDto> QueueBuildAsync(int definitionId, string? branch, CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured)
        {
            return new TriggeredBuildDto { DefinitionId = definitionId, Ok = false, Error = "Azure DevOps credentials not configured." };
        }

        try
        {
            object body = string.IsNullOrWhiteSpace(branch)
                ? new { definition = new { id = definitionId } }
                : new { definition = new { id = definitionId }, sourceBranch = NormalizeBranch(branch) };
            var result = await PostJsonAsync(BuildClient(),
                $"{EncodedProject}/_apis/build/builds?api-version={_apiVersion}", body, ct);

            var buildId = Int(result, "id");
            return new TriggeredBuildDto
            {
                DefinitionId = definitionId,
                Ok = true,
                BuildId = buildId,
                BuildNumber = Str(result, "buildNumber"),
                Status = Str(result, "status"),
                Url = buildId is null ? null : $"https://dev.azure.com/{_org}/{EncodedProject}/_build/results?buildId={buildId}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps queue build failed for definition {DefinitionId}", definitionId);
            return new TriggeredBuildDto { DefinitionId = definitionId, Ok = false, Error = ex.Message };
        }
    }

    // Deletes a build pipeline definition entirely. Irreversible on the Azure DevOps side
    // (no undo) — this is why the chat confirm-first flow exists before ever reaching here.
    public async Task<PipelineActionResultDto> DeleteBuildDefinitionAsync(int definitionId, string name, CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured)
        {
            return new PipelineActionResultDto { DefinitionId = definitionId, Name = name, Ok = false, Error = "Azure DevOps credentials not configured." };
        }

        try
        {
            await DeleteAsync(BuildClient(), $"{EncodedProject}/_apis/build/definitions/{definitionId}?api-version={_apiVersion}", ct);
            return new PipelineActionResultDto { DefinitionId = definitionId, Name = name, Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps delete build definition failed for {DefinitionId}", definitionId);
            return new PipelineActionResultDto { DefinitionId = definitionId, Name = name, Ok = false, Error = ex.Message };
        }
    }

    // Renames a build pipeline definition. Azure DevOps has no partial-rename endpoint — a
    // definition update requires GET-modify-PUT of the full definition body, including its
    // current revision number (the API rejects a PUT with a stale revision).
    public async Task<PipelineActionResultDto> RenameBuildDefinitionAsync(int definitionId, string oldName, string newName, CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured)
        {
            return new PipelineActionResultDto { DefinitionId = definitionId, Name = oldName, Ok = false, Error = "Azure DevOps credentials not configured." };
        }

        try
        {
            var client = BuildClient();
            var definition = await GetJsonAsync(client, $"{EncodedProject}/_apis/build/definitions/{definitionId}?api-version={_apiVersion}", ct);

            using var doc = JsonDocument.Parse(definition.GetRawText());
            var editable = new Dictionary<string, JsonElement>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                editable[prop.Name] = prop.Value;
            }
            editable["name"] = JsonSerializer.SerializeToElement(newName);

            await PutJsonAsync(client, $"{EncodedProject}/_apis/build/definitions/{definitionId}?api-version={_apiVersion}", editable, ct);
            return new PipelineActionResultDto { DefinitionId = definitionId, Name = newName, Ok = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps rename build definition failed for {DefinitionId}", definitionId);
            return new PipelineActionResultDto { DefinitionId = definitionId, Name = oldName, Ok = false, Error = ex.Message };
        }
    }

    // Same error-handling contract as PostJsonAsync/PatchJsonAsync, for full-body PUT updates.
    private static async Task<JsonElement> PutJsonAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await client.PutAsync(url, content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var hint = (int)resp.StatusCode switch
            {
                401 or 203 => "authentication failed — check the PAT (and that it has Build: Read & execute scope).",
                403 => "forbidden — the PAT likely lacks Build: Read & execute scope.",
                404 => "not found — check the build definition id, organization, and project.",
                _ => "request rejected by Azure DevOps."
            };
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {hint}");
        }

        using var doc = JsonDocument.Parse(respBody);
        return doc.RootElement.Clone();
    }

    // Same error-handling contract as the other write helpers, for HTTP DELETE calls
    // (no response body expected on success — Azure DevOps returns 204).
    private static async Task DeleteAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await client.DeleteAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            var hint = (int)resp.StatusCode switch
            {
                401 or 203 => "authentication failed — check the PAT (and that it has Build: Read & execute scope).",
                403 => "forbidden — the PAT likely lacks Build: Read & execute scope.",
                404 => "not found — check the build definition id, organization, and project.",
                _ => "request rejected by Azure DevOps."
            };
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {hint}. Body: {respBody}");
        }
    }

    // Cancels a running/queued build (sets status to "cancelling" — Azure DevOps then stops
    // it asynchronously; the build doesn't necessarily stop instantly on this call returning).
    public async Task<TriggeredBuildDto> CancelBuildAsync(int buildId, CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured)
        {
            return new TriggeredBuildDto { BuildId = buildId, Ok = false, Error = "Azure DevOps credentials not configured." };
        }

        try
        {
            var result = await PatchJsonAsync(BuildClient(),
                $"{EncodedProject}/_apis/build/builds/{buildId}?api-version={_apiVersion}",
                new { status = "cancelling" }, ct);

            return new TriggeredBuildDto
            {
                BuildId = buildId,
                Ok = true,
                Status = Str(result, "status"),
                Url = $"https://dev.azure.com/{_org}/{EncodedProject}/_build/results?buildId={buildId}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps cancel build failed for build {BuildId}", buildId);
            return new TriggeredBuildDto { BuildId = buildId, Ok = false, Error = ex.Message };
        }
    }

    // Same error-handling contract as PostJsonAsync, for PATCH calls (cancelling a build).
    private static async Task<JsonElement> PatchJsonAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await client.PatchAsync(url, content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var hint = (int)resp.StatusCode switch
            {
                401 or 203 => "authentication failed — check the PAT (and that it has Build: Read & execute scope).",
                403 => "forbidden — the PAT likely lacks Build: Read & execute scope.",
                404 => "not found — check the build id, organization, and project.",
                _ => "request rejected by Azure DevOps."
            };
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {hint}");
        }

        var trimmed = respBody.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Received an HTML page instead of JSON — the PAT is likely invalid/expired or lacks scope.");
        }

        using var doc = JsonDocument.Parse(respBody);
        return doc.RootElement.Clone();
    }

    // Failed-task timeline for one build — mirrors Sentinel's getBuildTimeline.
    public async Task<List<BuildErrorRecordDto>> GetBuildErrorsAsync(int buildId, CancellationToken ct)
    {
        await LoadConfigAsync(ct);
        if (!IsConfigured) return new();
        try
        {
            var root = await GetJsonAsync(BuildClient(),
                $"{EncodedProject}/_apis/build/builds/{buildId}/timeline?api-version={_apiVersion}", ct);

            if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
                return new();

            return records.EnumerateArray()
                .Where(r =>
                {
                    var result = Str(r, "result");
                    var hasIssues = r.TryGetProperty("issues", out var iss) && iss.ValueKind == JsonValueKind.Array && iss.GetArrayLength() > 0;
                    return result == "failed" || hasIssues;
                })
                .Select(r => new BuildErrorRecordDto
                {
                    Name = Str(r, "name"),
                    Type = Str(r, "type"),
                    Result = Str(r, "result"),
                    State = Str(r, "state"),
                    StartTime = Date(r, "startTime"),
                    FinishTime = Date(r, "finishTime"),
                    Issues = r.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array
                        ? issues.EnumerateArray().Select(i => new BuildIssueDto
                        {
                            Type = Str(i, "type"),
                            Category = Str(i, "category"),
                            Message = Str(i, "message")
                        }).ToList()
                        : new List<BuildIssueDto>()
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DevOps build timeline fetch failed for build {BuildId}", buildId);
            return new();
        }
    }
}
