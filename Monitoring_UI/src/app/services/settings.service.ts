import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface AzureDevOpsSettings {
  org: string | null;
  project: string | null;
  apiVersion: string | null;
  patConfigured: boolean;
}

export interface SettingsResponse {
  azureDevOps: AzureDevOpsSettings;
  ocelot: { directory: string };
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
}
