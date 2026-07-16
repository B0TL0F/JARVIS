export interface CheckResult {
  isUp: boolean;
  responseTimeMs: number | null;
  httpStatusCode: number | null;
  errorMessage: string | null;
  timestampUtc: string | null;
}

export interface ServiceStatus {
  module: string;
  api: CheckResult | null;
  db: CheckResult | null;
  uptimePercent24h: number;
}

export interface StatusResponse {
  environment: string;
  pollingIntervalSeconds: number;
  generatedAtUtc: string;
  modules: ServiceStatus[];
}

export interface EnvironmentsResponse {
  environments: string[];
  pollingIntervalSeconds: number;
}
