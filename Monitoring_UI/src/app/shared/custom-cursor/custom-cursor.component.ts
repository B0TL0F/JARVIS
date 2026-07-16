import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import gsap from 'gsap';

// A JARVIS targeting-reticle cursor. Three synced layers:
//   1. cursor-dot     — snaps instantly to the pointer (precise hit indicator)
//   2. cursor-blades  — 4 crosshair blades that trail slightly
//   3. cursor-ring    — outer rotating reticle with tick segments + brackets,
//                       trails smoothly behind the pointer for a "targeting"
//                       feel. Expands and locks onto interactive elements.
// Everything starts hidden until the first real mousemove so it never
// renders "stuck" in a corner if JS is slow to attach.
@Component({
  selector: 'app-custom-cursor',
  standalone: true,
  templateUrl: './custom-cursor.component.html',
  styleUrl: './custom-cursor.component.css'
})
export class CustomCursorComponent implements AfterViewInit, OnDestroy {
  @ViewChild('dot', { static: true }) dotRef!: ElementRef<HTMLElement>;
  @ViewChild('ring', { static: true }) ringRef!: ElementRef<HTMLElement>;
  @ViewChild('blades', { static: true }) bladesRef!: ElementRef<HTMLElement>;

  private moveRingX?: (v: number) => void;
  private moveRingY?: (v: number) => void;
  private moveBladesX?: (v: number) => void;
  private moveBladesY?: (v: number) => void;
  private armed = false;
  private readonly onMove = (e: MouseEvent) => this.handleMove(e);
  private readonly onDown = () => this.setPressed(true);
  private readonly onUp = () => this.setPressed(false);
  private readonly onOver = (e: MouseEvent) => this.handleOver(e);

  ngAfterViewInit(): void {
    document.body.classList.add('has-custom-cursor');

    gsap.set(
      [this.dotRef.nativeElement, this.ringRef.nativeElement, this.bladesRef.nativeElement],
      { xPercent: -50, yPercent: -50 }
    );

    // Blades trail a hair, ring trails more — layered depth of movement.
    this.moveBladesX = gsap.quickTo(this.bladesRef.nativeElement, 'x', { duration: 0.10, ease: 'power3.out' });
    this.moveBladesY = gsap.quickTo(this.bladesRef.nativeElement, 'y', { duration: 0.10, ease: 'power3.out' });
    this.moveRingX = gsap.quickTo(this.ringRef.nativeElement, 'x', { duration: 0.22, ease: 'power3.out' });
    this.moveRingY = gsap.quickTo(this.ringRef.nativeElement, 'y', { duration: 0.22, ease: 'power3.out' });

    document.addEventListener('mousemove', this.onMove);
    document.addEventListener('mousedown', this.onDown);
    document.addEventListener('mouseup', this.onUp);
    document.addEventListener('mouseover', this.onOver);
  }

  ngOnDestroy(): void {
    document.body.classList.remove('has-custom-cursor');
    document.removeEventListener('mousemove', this.onMove);
    document.removeEventListener('mousedown', this.onDown);
    document.removeEventListener('mouseup', this.onUp);
    document.removeEventListener('mouseover', this.onOver);
  }

  private handleMove(e: MouseEvent): void {
    if (!this.armed) {
      this.armed = true;
      this.dotRef.nativeElement.classList.add('armed');
      this.ringRef.nativeElement.classList.add('armed');
      this.bladesRef.nativeElement.classList.add('armed');
    }
    gsap.set(this.dotRef.nativeElement, { x: e.clientX, y: e.clientY });
    this.moveBladesX?.(e.clientX);
    this.moveBladesY?.(e.clientY);
    this.moveRingX?.(e.clientX);
    this.moveRingY?.(e.clientY);
  }

  private setPressed(pressed: boolean): void {
    this.ringRef.nativeElement.classList.toggle('pressed', pressed);
    this.bladesRef.nativeElement.classList.toggle('pressed', pressed);
  }

  private handleOver(e: MouseEvent): void {
    const interactive = (e.target as HTMLElement)?.closest(
      'button, a, input, select, [role="button"], .magnetic, .env-tab, .tabs button, .navtab, .header-btn'
    );
    this.ringRef.nativeElement.classList.toggle('hover', !!interactive);
    this.bladesRef.nativeElement.classList.toggle('hover', !!interactive);
    this.dotRef.nativeElement.classList.toggle('hover', !!interactive);
  }
}
