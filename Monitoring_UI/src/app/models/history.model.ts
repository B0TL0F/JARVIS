export type CheckType = 'Api' | 'Db';

export interface Incident {
  module: string;
  checkType: CheckType;
  startedAtUtc: string;
  resolvedAtUtc: string | null; // null = still down
  durationMinutes: number;
  errorMessage: string | null;
  httpStatusCode: number | null;
  sampleCount: number;
  summary: string;
}

export interface CheckBucket {
  bucketStartUtc: string;
  isUp: boolean;
  sampleCount: number;
}

export interface ModuleInsight {
  module: string;
  isFlaky: boolean;
  flipsLastHour: number;
  isResponseTimeAnomalous: boolean;
  latestResponseTimeMs: number | null;
  baselineResponseTimeMs: number | null;
  trend: 'improving' | 'stable' | 'degrading';
  uptimeLast24h: number;
  uptimePrevious24h: number;
}
