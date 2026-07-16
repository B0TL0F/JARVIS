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
    const result = await this.verify(username, password);
    if (!result.success) return { success: false, error: result.error };
    this.commit(result.role!, result.encoded!);
    return { success: true };
  }

  // Two-phase login used by the cinematic sign-in flow. `verify()` performs
  // the network call and returns the encoded credentials + role without
  // touching the auth signal (so the login screen stays mounted). `commit()`
  // is called AFTER the transition intro overlay is on-screen, flipping
  // the auth signal and letting the dashboard mount underneath the intro.
  async verify(username: string, password: string): Promise<{ success: boolean; error?: string; role?: Role; encoded?: string }> {
    const encoded = btoa(`${username}:${password}`);
    try {
      const me = await firstValueFrom(
        this.http.get<Me>('/api/me', { headers: { Authorization: `Basic ${encoded}` } })
      );
      return { success: true, role: me.role, encoded };
    } catch (err: any) {
      if (err?.status === 401) {
        return { success: false, error: 'Wrong username or password.' };
      }
      return { success: false, error: 'Could not reach Jarvis. Is the stack running?' };
    }
  }

  commit(role: Role, encoded?: string): void {
    if (encoded) sessionStorage.setItem(STORAGE_KEY, encoded);
    sessionStorage.setItem(ROLE_KEY, role);
    this._role.set(role);
    this._isAuthenticated.set(true);
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
