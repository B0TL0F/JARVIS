import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable, interval, startWith, switchMap, catchError, of, firstValueFrom } from 'rxjs';
import { EnvironmentsResponse, StatusResponse } from '../models/status.model';

@Injectable({ providedIn: 'root' })
export class StatusService {
  private readonly pollMs = 60000;
  private readonly _environment = new BehaviorSubject<string | null>(null);
  readonly environment$ = this._environment.asObservable();

  constructor(private http: HttpClient) {}

  async loadEnvironments(): Promise<string[]> {
    const res = await firstValueFrom(this.http.get<EnvironmentsResponse>('/api/environments'));
    if (!this._environment.value && res.environments.length > 0) {
      this._environment.next(res.environments[0]);
    }
    return res.environments;
  }

  selectEnvironment(name: string): void {
    this._environment.next(name);
  }

  watchStatus(): Observable<StatusResponse | null> {
    return this.environment$.pipe(
      switchMap((env) =>
        env === null
          ? of(null)
          : interval(this.pollMs).pipe(
              startWith(0),
              switchMap(() => this.http.get<StatusResponse>('/api/status', { params: { environment: env } })),
              catchError((err) => {
                console.error('Failed to fetch status', err);
                return of(null);
              })
            )
      )
    );
  }
}
