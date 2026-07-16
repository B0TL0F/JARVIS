export interface BuildSummary {
  id: number;
  number: string | null;
  result: string | null;
  finishTime: string | null;
  branch: string | null;
  requestedBy: string | null;
  url: string;
}

export interface BuildPipeline {
  id: number;
  name: string;
  path: string;
  queueStatus: string | null;
  latestStatus: string;
  latestBuildId: number | null;
  latestBuildNumber: string | null;
  latestFinished: string | null;
  latestBranch: string | null;
  latestRequestedBy: string | null;
  recentBuilds: BuildSummary[];
}

export interface ReleaseEnv {
  name: string;
  status: string | null;
}

export interface ReleaseSummary {
  id: number;
  name: string | null;
  status: string | null;
  createdOn: string | null;
  environments: ReleaseEnv[];
  url: string;
}

export interface ReleasePipeline {
  id: number;
  name: string;
  path: string;
  environments: string[];
  latestStatus: string;
  latestReleaseName: string | null;
  latestFinished: string | null;
  latestEnvironments: ReleaseEnv[];
  recentReleases: ReleaseSummary[];
}

export interface PipelineDashboard {
  ok: boolean;
  msg: string | null;
  buildPipelines: BuildPipeline[];
  releasePipelines: ReleasePipeline[];
}

export interface BuildIssue {
  type: string | null;
  category: string | null;
  message: string | null;
}

export interface BuildErrorRecord {
  name: string | null;
  type: string | null;
  result: string | null;
  state: string | null;
  startTime: string | null;
  finishTime: string | null;
  issues: BuildIssue[];
}

export interface BuildInsight {
  definitionId: number;
  name: string;
  successRate7d: number;
  successRate30d: number;
  isFlaky: boolean;
  flipCount: number;
  isDurationAnomalous: boolean;
  latestDurationMinutes: number | null;
  baselineDurationMinutes: number | null;
  consecutiveFailures: number;
  trend: 'improving' | 'stable' | 'degrading';
}

export interface ReleaseInsight {
  definitionId: number;
  name: string;
  deploymentsPerWeek: number;
  changeFailureRatePercent: number;
  mttrMinutes: number | null;
  consecutiveFailures: number;
}

export interface PipelineInsights {
  ok: boolean;
  msg: string | null;
  buildInsights: BuildInsight[];
  releaseInsights: ReleaseInsight[];
}

export interface TriggeredBuild {
  definitionId: number;
  name: string;
  ok: boolean;
  buildId: number | null;
  buildNumber: string | null;
  status: string | null;
  url: string | null;
  error: string | null;
}
