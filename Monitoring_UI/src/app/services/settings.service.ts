import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface AzureDevOpsSettings {
  org: string | null;
  project: string | null;
  apiVersion: string | null;
  patConfigured: boolean;
}

export interface ClaudeSettings {
  model: string | null;
  apiKeyConfigured: boolean;
}

export interface ClaudeUpdate {
  model?: string | null;
  apiKey?: string | null;
}

export interface GeminiSettings {
  model: string | null;
  apiKeyConfigured: boolean;
}

export interface GeminiUpdate {
  model?: string | null;
  apiKey?: string | null;
}

export interface GroqSettings {
  model: string | null;
  apiKeyConfigured: boolean;
}

export interface GroqUpdate {
  model?: string | null;
  apiKey?: string | null;
}

export interface AiProviderSettings {
  provider: string;
}

export interface AiProviderUpdate {
  provider: string;
}

export interface AlertSettings {
  enabled: boolean;
  teamsWebhookConfigured: boolean;
  smtpHost: string | null;
  smtpPort: number | null;
  smtpUsername: string | null;
  smtpFrom: string | null;
  smtpTo: string | null;
  smtpPasswordConfigured: boolean;
}

export interface AlertsUpdate {
  enabled: boolean;
  teamsWebhookUrl?: string | null;
  smtpHost?: string | null;
  smtpPort?: number | null;
  smtpUsername?: string | null;
  smtpPassword?: string | null;
  smtpFrom?: string | null;
  smtpTo?: string | null;
}

export interface SettingsResponse {
  azureDevOps: AzureDevOpsSettings;
  ocelot: { directory: string };
  aiProvider: AiProviderSettings;
  claude: ClaudeSettings;
  gemini: GeminiSettings;
  groq: GroqSettings;
  alerts: AlertSettings;
}

export interface AzureDevOpsUpdate {
  org: string | null;
  project: string | null;
  apiVersion: string | null;
  pat?: string | null;
}

export interface ReimportResult {
  imported: boolean;
  msg?: string;
  filesScanned?: number;
  environments: { environment: string; count: number }[];
}

@Injectable({ providedIn: 'root' })
export class SettingsService {
  constructor(private http: HttpClient) {}

  get(): Observable<SettingsResponse> {
    return this.http.get<SettingsResponse>('/api/settings');
  }

  updateAzure(update: AzureDevOpsUpdate): Observable<void> {
    return this.http.put<void>('/api/settings/azure-devops', update);
  }

  reimportOcelot(force: boolean): Observable<ReimportResult> {
    return this.http.post<ReimportResult>('/api/settings/ocelot/reimport', {}, { params: { force } });
  }

  updateClaude(update: ClaudeUpdate): Observable<void> {
    return this.http.put<void>('/api/settings/claude', update);
  }

  updateGemini(update: GeminiUpdate): Observable<void> {
    return this.http.put<void>('/api/settings/gemini', update);
  }

  updateAiProvider(update: AiProviderUpdate): Observable<void> {
    return this.http.put<void>('/api/settings/ai-provider', update);
  }

  updateGroq(update: GroqUpdate): Observable<void> {
    return this.http.put<void>('/api/settings/groq', update);
  }

  updateAlerts(update: AlertsUpdate): Observable<void> {
    return this.http.put<void>('/api/settings/alerts', update);
  }
}
