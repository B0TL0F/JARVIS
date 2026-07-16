import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { BuildErrorRecord, PipelineDashboard, PipelineInsights, TriggeredBuild } from '../models/pipeline.model';

@Injectable({ providedIn: 'root' })
export class PipelineService {
  constructor(private http: HttpClient) {}

  getDashboard(): Observable<PipelineDashboard> {
    return this.http.get<PipelineDashboard>('/api/pipelines');
  }

  getBuildErrors(buildId: number): Observable<BuildErrorRecord[]> {
    return this.http.get<BuildErrorRecord[]>(`/api/pipelines/build/${buildId}/errors`);
  }

  getInsights(): Observable<PipelineInsights> {
    return this.http.get<PipelineInsights>('/api/pipelines/insights');
  }

  triggerBuilds(definitionIds: number[], branch?: string): Observable<TriggeredBuild[]> {
    return this.http.post<TriggeredBuild[]>('/api/pipelines/trigger', { definitionIds, branch: branch || null });
  }
}
