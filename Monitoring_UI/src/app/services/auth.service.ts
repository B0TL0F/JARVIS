import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { Me, Role } from '../models/admin.model';

const STORAGE_KEY = 'monitoring.credentials';
const ROLE_KEY = 'monitoring.role';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _isAuthenticated = signal<boolean>(!!this.readStoredCredentials());
  readonly isAuthenticated = this._isAuthenticated.asReadonly();

  // Role drives which nav/views are shown (admin gets Users + Activity).
  private readonly _role = signal<Role | null>(this.readStoredRole());
  readonly role = this._role.asReadonly();

  constructor(private http: HttpClient) {}

  isAdmin(): boolean {
    return this._role() === 'admin';
  }

  getAuthHeader(): string | null {
    const creds = this.readStoredCredentials();
    return creds ? `Basic ${creds}` : null;
  }

  async login(username: string, password: string): Promise<{ success: boolean; error?: string }> {
    const encoded = btoa(`${username}:${password}`);
    try {
      // /api/me validates the credentials and returns the role (and records the
      // login in the activity log server-side).
      const me = await firstValueFrom(
        this.http.get<Me>('/api/me', { headers: { Authorization: `Basic ${encoded}` } })
      );
      sessionStorage.setItem(STORAGE_KEY, encoded);
      sessionStorage.setItem(ROLE_KEY, me.role);
      this._role.set(me.role);
      this._isAuthenticated.set(true);
      return { success: true };
    } catch (err: any) {
      if (err?.status === 401) {
        return { success: false, error: 'Wrong username or password.' };
      }
      return { success: false, error: 'Could not reach Jarvis. Is the stack running?' };
    }
  }

  logout(): void {
    this.clear();
  }

  onUnauthorized(): void {
    this.clear();
  }

  private clear(): void {
    sessionStorage.removeItem(STORAGE_KEY);
    sessionStorage.removeItem(ROLE_KEY);
    this._role.set(null);
    this._isAuthenticated.set(false);
  }

  private readStoredCredentials(): string | null {
    return sessionStorage.getItem(STORAGE_KEY);
  }

  private readStoredRole(): Role | null {
    const r = sessionStorage.getItem(ROLE_KEY);
    return r === 'admin' || r === 'developer' ? r : null;
  }
}
