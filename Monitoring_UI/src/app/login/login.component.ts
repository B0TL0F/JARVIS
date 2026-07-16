import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css'
})
export class LoginComponent {
  username = '';
  password = '';
  error: string | null = null;
  submitting = false;

  constructor(private auth: AuthService) {}

  async submit(): Promise<void> {
    if (!this.username || !this.password || this.submitting) return;
    this.submitting = true;
    this.error = null;
    const result = await this.auth.login(this.username, this.password);
    this.submitting = false;
    if (!result.success) {
      this.error = result.error ?? 'Sign-in failed.';
    }
  }
}
