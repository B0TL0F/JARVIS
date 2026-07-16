import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ActivityLogPage, CreateUserRequest, User } from '../models/admin.model';

@Injectable({ providedIn: 'root' })
export class AdminService {
  constructor(private http: HttpClient) {}

  getUsers(): Observable<User[]> {
    return this.http.get<User[]>('/api/users');
  }

  addUser(req: CreateUserRequest): Observable<User> {
    return this.http.post<User>('/api/users', req);
  }

  deleteUser(id: number): Observable<void> {
    return this.http.delete<void>(`/api/users/${id}`);
  }

  getActivity(page = 1, pageSize = 50): Observable<ActivityLogPage> {
    return this.http.get<ActivityLogPage>('/api/activity', { params: { page, pageSize } });
  }
}
