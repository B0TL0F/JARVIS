import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../services/auth.service';
import { LoginIntroService } from '../services/login-intro.service';
import { HudGlobeComponent } from '../shared/hud-globe/hud-globe.component';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [CommonModule, FormsModule, HudGlobeComponent],
  templateUrl: './login.component.html',
  styleUrl: './login.component.css'
})
export class LoginComponent {
  username = '';
  password = '';
  error: string | null = null;
  submitting = false;

  constructor(private auth: AuthService, private intro: LoginIntroService) {}

  async submit(): Promise<void> {
    if (!this.username || !this.password || this.submitting) return;
    this.submitting = true;
    this.error = null;
    const result = await this.auth.login(this.username, this.password);
    if (!result.success) {
      this.submitting = false;
      this.error = result.error ?? 'Sign-in failed.';
      return;
    }
    // Keep the submitting flag on while the cinematic transition plays so
    // the button doesn't briefly re-enable and re-flash "Enter Jarvis".
    await this.intro.play();
    this.submitting = false;
  }
}
