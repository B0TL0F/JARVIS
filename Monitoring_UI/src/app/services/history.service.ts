import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CheckBucket, CheckType, Incident, ModuleInsight } from '../models/history.model';

@Injectable({ providedIn: 'root' })
export class HistoryService {
  constructor(private http: HttpClient) {}

  getIncidents(environment: string, module?: string): Observable<Incident[]> {
    let params = new HttpParams().set('environment', environment);
    if (module) params = params.set('module', module);
    return this.http.get<Incident[]>('/api/history/incidents', { params });
  }

  getChecks(environment: string, module: string, checkType: CheckType, hours = 24): Observable<CheckBucket[]> {
    const params = new HttpParams()
      .set('environment', environment)
      .set('module', module)
      .set('checkType', checkType)
      .set('hours', hours);
    return this.http.get<CheckBucket[]>('/api/history/checks', { params });
  }

  getInsights(environment: string): Observable<ModuleInsight[]> {
    const params = new HttpParams().set('environment', environment);
    return this.http.get<ModuleInsight[]>('/api/history/insights', { params });
  }
}
