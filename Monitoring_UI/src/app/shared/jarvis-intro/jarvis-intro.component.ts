import { AfterViewInit, Component, ElementRef, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HudGlobeComponent } from '../hud-globe/hud-globe.component';
import gsap from 'gsap';

// Cinematic bridge between the login screen and the dashboard.
// A full-screen overlay that:
//   1. Materialises the globe at ~10% scale near the login card position
//   2. Zooms it to fill the centre of the screen (~85% scale) with a bright
//      HUD "ACCESS GRANTED" label and radial burst
//   3. Contracts and drifts up-right into the dashboard globe-stage position
//      while the iris fades and the underlying dashboard is revealed
// Timing is coordinated with LoginIntroService.play() (default 2.4s).
@Component({
  selector: 'app-jarvis-intro',
  standalone: true,
  imports: [CommonModule, HudGlobeComponent],
  templateUrl: './jarvis-intro.component.html',
  styleUrl: './jarvis-intro.component.css'
})
export class JarvisIntroComponent implements AfterViewInit {
  @ViewChild('stage', { static: true }) stageRef!: ElementRef<HTMLElement>;
  @ViewChild('label', { static: true }) labelRef!: ElementRef<HTMLElement>;
  @ViewChild('burst', { static: true }) burstRef!: ElementRef<HTMLElement>;
  @ViewChild('overlay', { static: true }) overlayRef!: ElementRef<HTMLElement>;
  @ViewChild('rings', { static: true }) ringsRef!: ElementRef<HTMLElement>;

  private readonly reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  ngAfterViewInit(): void {
    if (this.reducedMotion) return;

    const stage = this.stageRef.nativeElement;
    const label = this.labelRef.nativeElement;
    const burst = this.burstRef.nativeElement;
    const overlay = this.overlayRef.nativeElement;
    const rings = this.ringsRef.nativeElement;

    // The starting position is roughly where the login card sat (viewport
    // centre-left). The ending position is roughly where the dashboard's
    // globe stage renders (viewport centre — since the HUD dashboard puts
    // the globe in the middle-top of the grid, we settle at a slight
    // upward offset for a natural land).
    const tl = gsap.timeline();

    tl.fromTo(
      overlay,
      { opacity: 0 },
      { opacity: 1, duration: 0.25, ease: 'power2.out' }
    );

    tl.fromTo(
      stage,
      { scale: 0.15, opacity: 0, x: 0, y: 20, rotate: -8 },
      { scale: 1, opacity: 1, rotate: 0, duration: 0.9, ease: 'power3.out' },
      0.1
    );

    tl.fromTo(
      rings,
      { opacity: 0, scale: 0.4 },
      { opacity: 1, scale: 1, duration: 0.8, ease: 'power2.out' },
      0.15
    );

    tl.fromTo(
      burst,
      { opacity: 0, scale: 0.2 },
      { opacity: 1, scale: 3.2, duration: 0.7, ease: 'power3.out' },
      0.35
    ).to(burst, { opacity: 0, duration: 0.5, ease: 'power2.in' }, '>-0.2');

    tl.fromTo(
      label,
      { opacity: 0, y: 14, letterSpacing: '1em' },
      { opacity: 1, y: 0, letterSpacing: '0.5em', duration: 0.6, ease: 'power3.out' },
      0.7
    ).to(label, { opacity: 0, y: -6, duration: 0.35, ease: 'power2.in' }, 1.55);

    // Contract + drift toward the dashboard globe slot (upper-center of the
    // grid, so a small y shift), then fade the whole overlay.
    tl.to(
      stage,
      { scale: 0.36, y: -80, duration: 0.7, ease: 'power2.inOut' },
      1.55
    );
    tl.to(rings, { opacity: 0, scale: 1.6, duration: 0.6, ease: 'power2.in' }, 1.55);
    tl.to(overlay, { opacity: 0, duration: 0.45, ease: 'power2.in' }, 1.95);
  }
}
