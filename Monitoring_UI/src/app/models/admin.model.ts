export type Role = 'admin' | 'developer';

export interface Me {
  id: number | null;
  username: string;
  role: Role;
}

export interface User {
  id: number;
  username: string;
  role: Role;
  createdAtUtc: string;
}

export interface CreateUserRequest {
  username: string;
  password: string;
  role: Role;
}

export interface ActivityLog {
  id: number;
  userId: number | null;
  username: string;
  action: string;
  details: string;
  ip: string | null;
  createdAtUtc: string;
}

export interface ActivityLogPage {
  page: number;
  pageSize: number;
  total: number;
  items: ActivityLog[];
}
