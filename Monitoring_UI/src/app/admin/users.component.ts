import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AdminService } from '../services/admin.service';
import { Role, User } from '../models/admin.model';

@Component({
  selector: 'app-users',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './users.component.html',
  styleUrl: './admin.css'
})
export class UsersComponent implements OnInit {
  users: User[] = [];
  loading = true;
  error: string | null = null;

  newUsername = '';
  newPassword = '';
  newRole: Role = 'developer';
  submitting = false;

  constructor(private admin: AdminService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading = true;
    this.admin.getUsers().subscribe({
      next: (u) => { this.users = u; this.loading = false; },
      error: () => { this.error = 'Failed to load users.'; this.loading = false; }
    });
  }

  add(): void {
    if (!this.newUsername || !this.newPassword || this.submitting) return;
    this.submitting = true;
    this.error = null;
    this.admin.addUser({ username: this.newUsername, password: this.newPassword, role: this.newRole }).subscribe({
      next: () => {
        this.newUsername = '';
        this.newPassword = '';
        this.newRole = 'developer';
        this.submitting = false;
        this.load();
      },
      error: (err) => {
        this.error = err?.error?.error ?? 'Failed to create user.';
        this.submitting = false;
      }
    });
  }

  remove(user: User): void {
    if (!confirm(`Delete user "${user.username}"?`)) return;
    this.error = null;
    this.admin.deleteUser(user.id).subscribe({
      next: () => this.load(),
      error: (err) => this.error = err?.error?.error ?? 'Failed to delete user.'
    });
  }

  trackById = (_i: number, u: User) => u.id;
}
