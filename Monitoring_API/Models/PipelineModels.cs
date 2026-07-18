namespace Monitoring_API.Models;

// DTOs for the Azure DevOps pipeline dashboard (ported from Sentinel's
// azure-devops.js getPipelineDashboard output). Shapes mirror the JSON the
// Sentinel Vue UI consumed so the Angular port can render the same fields.

public class PipelineDashboardDto
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
    public List<BuildPipelineDto> BuildPipelines { get; set; } = new();
    public List<ReleasePipelineDto> ReleasePipelines { get; set; } = new();
}

public class BuildPipelineDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = "\\";
    public string? QueueStatus { get; set; }
    public string LatestStatus { get; set; } = "none";
    public int? LatestBuildId { get; set; }
    public string? LatestBuildNumber { get; set; }
    public DateTime? LatestFinished { get; set; }
    public string? LatestBranch { get; set; }
    public string? LatestRequestedBy { get; set; }
    public List<BuildSummaryDto> RecentBuilds { get; set; } = new();
}

public class BuildSummaryDto
{
    public int Id { get; set; }
    public string? Number { get; set; }
    public string? Result { get; set; }
    public DateTime? FinishTime { get; set; }
    public string? Branch { get; set; }
    public string? RequestedBy { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class ReleasePipelineDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = "\\";
    public List<string> Environments { get; set; } = new();
    public string LatestStatus { get; set; } = "none";
    public string? LatestReleaseName { get; set; }
    public DateTime? LatestFinished { get; set; }
    public List<ReleaseEnvDto> LatestEnvironments { get; set; } = new();
    public List<ReleaseSummaryDto> RecentReleases { get; set; } = new();
}

public class ReleaseSummaryDto
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Status { get; set; }
    public DateTime? CreatedOn { get; set; }
    public List<ReleaseEnvDto> Environments { get; set; } = new();
    public string Url { get; set; } = string.Empty;
}

public class ReleaseEnvDto
{
    public string Name { get; set; } = string.Empty;
    public string? Status { get; set; }
}

public class BuildErrorRecordDto
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public string? Result { get; set; }
    public string? State { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? FinishTime { get; set; }
    public List<BuildIssueDto> Issues { get; set; } = new();
}

public class BuildIssueDto
{
    public string? Type { get; set; }
    public string? Category { get; set; }
    public string? Message { get; set; }
}

// One build/release fetched for insight computation — a superset of
// BuildSummaryDto/ReleaseSummaryDto with the fields (result, start/finish
// time) PipelineAnalysisService needs but the dashboard cards don't show.
public class BuildHistoryItem
{
    public int Id { get; set; }
    public string? Result { get; set; }        // "succeeded" | "failed" | "partiallySucceeded" | "canceled"
    public DateTime? StartTime { get; set; }
    public DateTime? FinishTime { get; set; }
}

public class ReleaseHistoryItem
{
    public int Id { get; set; }
    public string? OverallStatus { get; set; }  // derived: "succeeded" | "failed" | "partiallySucceeded"
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

// Heuristic insights for one build pipeline definition — same statistical
// techniques as ModuleInsightDto (flip-counting, z-score, trend), applied to
// Azure DevOps build history instead of StatusChecks.
public class BuildInsightDto
{
    public int DefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public double SuccessRate7d { get; set; }
    public double SuccessRate30d { get; set; }
    public bool IsFlaky { get; set; }
    public int FlipCount { get; set; }
    public bool IsDurationAnomalous { get; set; }
    public double? LatestDurationMinutes { get; set; }
    public double? BaselineDurationMinutes { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string Trend { get; set; } = "stable"; // "improving" | "stable" | "degrading"
    public string? Summary { get; set; }
}

// DORA-style metrics for one release pipeline definition, computed directly
// from Azure DevOps release history (no local persistence needed — Azure
// already retains this as the system of record).
public class ReleaseInsightDto
{
    public int DefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public double DeploymentsPerWeek { get; set; }
    public double ChangeFailureRatePercent { get; set; }
    public double? MttrMinutes { get; set; }    // null if no failure has ever recovered in the window
    public int ConsecutiveFailures { get; set; }
}

// Request/response shapes for the bulk build-trigger feature — the one write
// operation Jarvis performs against Azure DevOps. Build pipelines only.
public class TriggerBuildsRequest
{
    public List<int> DefinitionIds { get; set; } = new();
    // Optional — applies to every pipeline in this batch. Null/empty means
    // each pipeline's own default branch (Azure DevOps's normal behavior).
    public string? Branch { get; set; }
}

public class TriggeredBuildDto
{
    public int DefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public int? BuildId { get; set; }
    public string? BuildNumber { get; set; }
    public string? Status { get; set; }
    public string? Url { get; set; }
    public string? Error { get; set; }
}

// Result of a pipeline-definition mutation (delete or rename) — a build definition,
// not a build run, so it has no BuildId/BuildNumber/Status like TriggeredBuildDto.
public class RenamePipelineRequest
{
    public string? OldName { get; set; }
    public string NewName { get; set; } = string.Empty;
}

public class PipelineActionResultDto
{
    public int DefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public string? Error { get; set; }
}
