import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';

// Iron-Man style JARVIS boot splash. Concentric HUD rings, radar sweep, arc
// reactor core, glitching wordmark, data-stream columns and a typewriter
// boot log — one cinematic moment on every fresh app load before the auth
// gate takes over.
@Component({
  selector: 'app-jarvis-boot',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './jarvis-boot.component.html',
  styleUrl: './jarvis-boot.component.css'
})
export class JarvisBootComponent implements OnInit, OnDestroy {
  visible = true;
  fading = false;

  readonly lines = [
    'BOOTING J.A.R.V.I.S. CORE...',
    'ESTABLISHING SECURE UPLINK...',
    'SYNCING 14 MICROSERVICE NODES...',
    'ALL SYSTEMS NOMINAL'
  ];
  displayedLines: string[] = [];
  currentLine = '';

  // Tick marks around the SVG arc ring — every 15 degrees.
  readonly ticks = Array.from({ length: 24 }, (_, i) => i * 15);
  readonly letters = ['J', 'A', 'R', 'V', 'I', 'S'];

  readonly streamLeft = [
    '0xA1F3 :: uplink armed',
    'auth::rbac.ok',
    'net.ingress.ready',
    'db.mongo.pool<12>',
    'pipeline.observer.on',
    'hb::env=QA.pass',
    'hb::env=DEV.pass',
    'ocelot.route.match',
    'gpu.shader.compiled'
  ];
  readonly streamRight = [
    'trace :: 2f9e-be71',
    'entropy 0.92 nom',
    'radar sweep +12°',
    'globe.mesh.128k',
    'satellite.beacon.ok',
    'signal.drift 0.001',
    'nav.tab.iris.ready',
    'motion.system.armed',
    'aegis.protocol.on'
  ];

  private readonly reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  private timers: ReturnType<typeof setTimeout>[] = [];

  ngOnInit(): void {
    if (this.reducedMotion) {
      this.displayedLines = [...this.lines];
      this.schedule(() => this.startFade(), 500);
      return;
    }
    this.typeLine(0);
  }

  ngOnDestroy(): void {
    this.timers.forEach((t) => clearTimeout(t));
  }

  private schedule(fn: () => void, delay: number): void {
    this.timers.push(setTimeout(fn, delay));
  }

  private typeLine(lineIndex: number): void {
    if (lineIndex >= this.lines.length) {
      this.schedule(() => this.startFade(), 400);
      return;
    }
    const line = this.lines[lineIndex];
    let charIndex = 0;
    const tick = () => {
      charIndex++;
      this.currentLine = line.slice(0, charIndex);
      if (charIndex < line.length) {
        this.schedule(tick, 14);
      } else {
        this.displayedLines = [...this.displayedLines, line];
        this.currentLine = '';
        this.schedule(() => this.typeLine(lineIndex + 1), 120);
      }
    };
    tick();
  }

  private startFade(): void {
    this.fading = true;
    this.schedule(() => (this.visible = false), 650);
  }
}
