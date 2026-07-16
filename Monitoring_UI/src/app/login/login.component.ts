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

    // Verify credentials first (small HTTP call). If they're wrong, no intro.
    // If they're right, immediately kick off the cinematic intro overlay
    // WHILE flipping the auth signal — this way the intro is mounted in the
    // same tick as the login → dashboard swap, and stays alive on top of
    // the dashboard for its full duration.
    const result = await this.auth.verify(this.username, this.password);
    if (!result.success) {
      this.submitting = false;
      this.error = result.error ?? 'Sign-in failed.';
      return;
    }

    // Kick off the intro FIRST, then flip auth. The intro overlay is
    // body-appended (bypassing Angular's CD) so it can't be prematurely
    // unmounted by unrelated change detection cycles. Passing the operator
    // name gives JARVIS a personal greeting during the ACCESS GRANTED beat.
    const introDone = this.intro.play({ operator: this.username });
    this.auth.commit(result.role!);

    await introDone;
    this.submitting = false;
  }
}
