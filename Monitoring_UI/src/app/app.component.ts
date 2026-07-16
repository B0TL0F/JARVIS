import { AfterViewChecked, Component, ElementRef, HostListener, Renderer2, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DashboardComponent } from './dashboard/dashboard.component';
import { LoginComponent } from './login/login.component';
import { PipelinesComponent } from './pipelines/pipelines.component';
import { UsersComponent } from './admin/users.component';
import { ActivityComponent } from './admin/activity.component';
import { SettingsComponent } from './settings/settings.component';
import { TargetsComponent } from './targets/targets.component';
import { AuthService } from './services/auth.service';
import { ViewTransitionService } from './services/view-transition.service';
import { VoiceService } from './services/voice.service';
import { MagneticDirective } from './shared/magnetic.directive';
import { CustomCursorComponent } from './shared/custom-cursor/custom-cursor.component';
import { JarvisBootComponent } from './shared/jarvis-boot/jarvis-boot.component';
import { HistoryComponent } from './history/history.component';

type View = 'dashboard' | 'pipelines' | 'history' | 'targets' | 'users' | 'activity' | 'settings';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [
    CommonModule,
    DashboardComponent,
    LoginComponent,
    PipelinesComponent,
    UsersComponent,
    ActivityComponent,
    SettingsComponent,
    TargetsComponent,
    MagneticDirective,
    CustomCursorComponent,
    JarvisBootComponent,
    HistoryComponent
  ],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css'
})
export class AppComponent implements AfterViewChecked {
  private _view: View = 'dashboard';
  private lastAnimatedView: View | null = null;

  // The nav pill list lives behind a hamburger button in the header —
  // menuOpen toggles its dropdown.
  menuOpen = false;

  @ViewChild('viewport') viewport?: ElementRef<HTMLElement>;
  @ViewChild('navMenu', { read: ElementRef }) navMenu?: ElementRef<HTMLElement>;

  private readonly reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  constructor(
    public auth: AuthService,
    public voice: VoiceService,
    private transitions: ViewTransitionService,
    private renderer: Renderer2,
    private hostRef: ElementRef<HTMLElement>
  ) {
    this.voiceMuted = this.voice.isMuted();
    this.syncHudModeClass();
  }

  voiceMuted = false;

  toggleVoice(): void {
    this.voiceMuted = !this.voiceMuted;
    this.voice.setMuted(this.voiceMuted);
    // If unmuting, give a tiny audible confirmation so the user knows it took.
    if (!this.voiceMuted) this.voice.speak('Voice online.', { rate: 1.0, pitch: 0.9 });
  }

  toggleMenu(): void {
    this.menuOpen = !this.menuOpen;
  }

  // Closes the nav dropdown when clicking anywhere outside it (or its
  // hamburger toggle button).
  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    // Global HUD ripple: any button/link/tab/magnetic gets one on click.
    this.spawnRipple(event);

    if (!this.menuOpen) return;
    const target = event.target as Node;
    if (this.navMenu && !this.navMenu.nativeElement.contains(target)) {
      this.menuOpen = false;
    }
  }

  private spawnRipple(event: MouseEvent): void {
    if (this.reducedMotion) return;
    const target = event.target as HTMLElement | null;
    if (!target) return;
    const host = target.closest<HTMLElement>(
      'button, a.magnetic, .env-tab, .navtab, .header-btn, .magnetic'
    );
    if (!host) return;
    host.classList.add('jv-ripple');
    const rect = host.getBoundingClientRect();
    const size = Math.max(rect.width, rect.height) * 1.8;
    const node = document.createElement('span');
    node.className = 'jv-ripple-node';
    node.style.width = `${size}px`;
    node.style.height = `${size}px`;
    node.style.left = `${event.clientX - rect.left}px`;
    node.style.top = `${event.clientY - rect.top}px`;
    host.appendChild(node);
    node.addEventListener('animationend', () => node.remove(), { once: true });
  }

  get view(): View {
    return this._view;
  }

  private set view(next: View) {
    this._view = next;
    this.syncHudModeClass();
  }

  // 'dashboard' is the HUD/globe home view (mirrors the reference's
  // body.hud-mode) — every other view is a normal tabbed page.
  private syncHudModeClass(): void {
    this.renderer[this._view === 'dashboard' ? 'addClass' : 'removeClass'](document.body, 'hud-mode');
  }

  ngAfterViewChecked(): void {
    // Runs after Angular has (re)created the active view's component, so the
    // freshly-mounted DOM is what we animate in.
    if (this.viewport && this.view !== this.lastAnimatedView) {
      this.lastAnimatedView = this.view;
      this.transitions.enter(this.viewport.nativeElement);
    }
  }

  go(view: View): void {
    // Admin-only views fall back to the dashboard for developers.
    if ((view === 'users' || view === 'activity' || view === 'settings' || view === 'targets') && !this.auth.isAdmin()) {
      this.view = 'dashboard';
      this.menuOpen = false;
      return;
    }
    this.view = view;
    this.menuOpen = false;
  }

  logout(): void {
    this.voice.speak('Session terminated. Goodbye.', { rate: 0.95, pitch: 0.85 });
    this.view = 'dashboard';
    this.auth.logout();
  }
}
