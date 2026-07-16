import { Injectable } from '@angular/core';
import gsap from 'gsap';
import { VoiceService } from './voice.service';

// Orchestrates the cinematic login → dashboard sequence by imperatively
// mounting a full-screen HUD overlay to <body>. Bypasses Angular's *ngIf +
// change detection entirely, so the overlay can never be prematurely
// unmounted by unrelated CD cycles (e.g. status polling on the dashboard).
// Timeline: globe materialises → burst peak → ACCESS GRANTED pulse →
// contract & drift into the dashboard globe slot → fade.
// If browser Speech Synthesis is available AND the user hasn't muted,
// JARVIS speaks "Welcome back, <Operator>. All systems nominal." timed to
// the ACCESS GRANTED moment.
@Injectable({ providedIn: 'root' })
export class LoginIntroService {
  private overlay: HTMLElement | null = null;

  constructor(private voice: VoiceService) {}

  play(opts: { operator?: string; durationMs?: number } = {}): Promise<void> {
    const durationMs = opts.durationMs ?? 2400;
    const operator = (opts.operator ?? '').trim();
    if (this.overlay) return Promise.resolve();
    if (typeof document === 'undefined') return Promise.resolve();

    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    const overlay = document.createElement('div');
    overlay.className = 'jv-intro-overlay';
    overlay.innerHTML = `
      <span class="jvi-corner tl"></span>
      <span class="jvi-corner tr"></span>
      <span class="jvi-corner bl"></span>
      <span class="jvi-corner br"></span>

      <div class="jvi-burst"></div>

      <div class="jvi-rings">
        <span class="jvi-ring r1"></span>
        <span class="jvi-ring r2"></span>
        <span class="jvi-ring r3"></span>
        <span class="jvi-tick m1"></span>
        <span class="jvi-tick m2"></span>
        <span class="jvi-tick m3"></span>
        <span class="jvi-tick m4"></span>
      </div>

      <div class="jvi-stage">
        <div class="jvi-sphere">
          <span class="jvi-mesh mesh-1"></span>
          <span class="jvi-mesh mesh-2"></span>
          <span class="jvi-mesh mesh-3"></span>
          <span class="jvi-core"></span>
        </div>
      </div>

      <div class="jvi-label">ACCESS &middot; GRANTED</div>
      <div class="jvi-status">// JARVIS UPLINK ESTABLISHED</div>
    `;
    document.body.appendChild(overlay);
    this.overlay = overlay;

    if (reducedMotion) {
      return new Promise((resolve) => {
        setTimeout(() => {
          overlay.remove();
          this.overlay = null;
          resolve();
        }, 400);
      });
    }

    const stage = overlay.querySelector<HTMLElement>('.jvi-stage')!;
    const rings = overlay.querySelector<HTMLElement>('.jvi-rings')!;
    const burst = overlay.querySelector<HTMLElement>('.jvi-burst')!;
    const label = overlay.querySelector<HTMLElement>('.jvi-label')!;
    const status = overlay.querySelector<HTMLElement>('.jvi-status')!;

    return new Promise((resolve) => {
      const tl = gsap.timeline({
        onComplete: () => {
          overlay.remove();
          this.overlay = null;
          resolve();
        }
      });

      // Fade the entire overlay in.
      tl.fromTo(overlay, { opacity: 0 }, { opacity: 1, duration: 0.25, ease: 'power2.out' });

      // Globe materialises from small + rotated.
      tl.fromTo(
        stage,
        { scale: 0.15, opacity: 0, rotate: -12, y: 30 },
        { scale: 1, opacity: 1, rotate: 0, y: 0, duration: 0.85, ease: 'power3.out' },
        0.1
      );

      // Reticle rings scale in behind the globe.
      tl.fromTo(
        rings,
        { opacity: 0, scale: 0.4 },
        { opacity: 1, scale: 1, duration: 0.8, ease: 'power2.out' },
        0.2
      );

      // Radial energy burst — expands and fades.
      tl.fromTo(
        burst,
        { opacity: 0, scale: 0.2 },
        { opacity: 1, scale: 3.4, duration: 0.7, ease: 'power3.out' },
        0.35
      ).to(burst, { opacity: 0, duration: 0.5, ease: 'power2.in' }, '>-0.2');

      // ACCESS · GRANTED pulses in with tracking-out letter-spacing.
      tl.fromTo(
        label,
        { opacity: 0, y: 14, letterSpacing: '1em' },
        {
          opacity: 1, y: 0, letterSpacing: '0.5em', duration: 0.6, ease: 'power3.out',
          onStart: () => {
            // Fire JARVIS's greeting the instant the label starts appearing.
            // Fire-and-forget — the visual keeps its beat even if speech is
            // unavailable/blocked/muted.
            const name = this.friendlyName(operator);
            const greeting = name
              ? `Welcome back, ${name}. All systems nominal.`
              : 'Welcome back. All systems nominal.';
            this.voice.speak(greeting);
          }
        },
        0.75
      );
      tl.fromTo(
        status,
        { opacity: 0, y: 8 },
        { opacity: 0.85, y: 0, duration: 0.5, ease: 'power2.out' },
        0.95
      );

      // Hold the label for a moment.
      tl.to({}, { duration: 0.5 });

      // Contract phase — globe shrinks and drifts upward toward the dashboard
      // globe stage position. Label + status fade out.
      tl.to(label,  { opacity: 0, y: -10, duration: 0.4, ease: 'power2.in' }, '>-0.05');
      tl.to(status, { opacity: 0, y: -6, duration: 0.4, ease: 'power2.in' }, '<');
      tl.to(stage,  { scale: 0.32, y: -90, duration: 0.7, ease: 'power2.inOut' }, '<');
      tl.to(rings,  { opacity: 0, scale: 1.6, duration: 0.6, ease: 'power2.in' }, '<');

      // Final overlay fade to reveal the dashboard.
      tl.to(overlay, { opacity: 0, duration: 0.5, ease: 'power2.in' }, '>-0.15');

      // Force the total to at least durationMs.
      const totalNow = tl.duration() * 1000;
      if (totalNow < durationMs) tl.to({}, { duration: (durationMs - totalNow) / 1000 });
    });
  }

  // Turn a raw operator id like "devops" or "sarah.chen" into something a
  // TTS engine can pronounce naturally. Keeps first-name-only, title-cases,
  // and gracefully returns "" if the id looks like a role/service account.
  private friendlyName(raw: string): string {
    if (!raw) return '';
    const clean = raw.replace(/[._-]+/g, ' ').trim().split(' ')[0];
    if (!clean) return '';
    if (['admin', 'root', 'devops', 'operator', 'system'].includes(clean.toLowerCase())) return '';
    return clean.charAt(0).toUpperCase() + clean.slice(1).toLowerCase();
  }
}
