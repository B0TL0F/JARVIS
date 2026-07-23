import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface ManagedTarget {
  // Null when the target only exists in the static config/targets.json seed
  // and has never been edited — there's no DB row yet. Edit it and this
  // becomes non-null (see TargetsService.override).
  id: number | null;
  environment: string;
  module: string;
  apiHost: string;
  routePrefix: string;
  origin: string;        // "config" | "manual" | "ocelot" | "override"
  sourceFile: string | null; // originating ocelot filename, if imported that way
  healthCheckUrl: string | null;
  createdAtUtc: string | null;
}

export interface TargetUpsert {
  environment: string;
  module: string;
  apiHost: string | null;
  routePrefix: string | null;
}

@Injectable({ providedIn: 'root' })
export class TargetsService {
  constructor(private http: HttpClient) {}

  list(): Observable<ManagedTarget[]> {
    return this.http.get<ManagedTarget[]>('/api/targets');
  }

  create(t: TargetUpsert): Observable<ManagedTarget> {
    return this.http.post<ManagedTarget>('/api/targets', t);
  }

  // Edits a target that's only defined in config/targets.json today — creates
  // a DB row that overrides the static config for this (environment, module).
  override(t: TargetUpsert): Observable<ManagedTarget> {
    return this.http.post<ManagedTarget>('/api/targets/override', t);
  }

  update(id: number, t: TargetUpsert): Observable<void> {
    return this.http.put<void>(`/api/targets/${id}`, t);
  }

  remove(id: number): Observable<void> {
    return this.http.delete<void>(`/api/targets/${id}`);
  }

  removeEnvironment(environment: string): Observable<void> {
    return this.http.delete<void>(`/api/targets/environment/${encodeURIComponent(environment)}`);
  }
}
