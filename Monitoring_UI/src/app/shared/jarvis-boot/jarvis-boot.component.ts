import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';

// A one-time "J.A.R.V.I.S. is booting" splash shown on every fresh app load,
// before the login/dashboard gate — concentric HUD rings converging plus a
// typewriter boot log. Reduced-motion gets a fast, simplified variant (full
// text immediately, no character typing, quick fade) rather than being
// skipped outright — the user explicitly wants this moment every time.
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
    'LOADING TELEMETRY GRID...',
    'CALIBRATING GROUND OPS UPLINK...',
    'ALL SYSTEMS NOMINAL'
  ];
  displayedLines: string[] = [];
  currentLine = '';

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
      this.schedule(() => this.startFade(), 500);
      return;
    }
    const line = this.lines[lineIndex];
    let charIndex = 0;
    const tick = () => {
      charIndex++;
      this.currentLine = line.slice(0, charIndex);
      if (charIndex < line.length) {
        this.schedule(tick, 26);
      } else {
        this.displayedLines = [...this.displayedLines, line];
        this.currentLine = '';
        this.schedule(() => this.typeLine(lineIndex + 1), 240);
      }
    };
    tick();
  }

  private startFade(): void {
    this.fading = true;
    this.schedule(() => (this.visible = false), 500);
  }
}
