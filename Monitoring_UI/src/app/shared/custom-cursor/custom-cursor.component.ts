import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import gsap from 'gsap';

// A red HUD targeting-reticle cursor: an inner dot that locks to the pointer
// instantly and an outer ring that trails smoothly behind it, brightening
// over anything clickable. This is pointer-tracking, not scroll/parallax
// motion, so it stays on even for prefers-reduced-motion users — it never
// disables itself, it just stays hidden (opacity 0) until the first real
// mousemove repositions it, so it can never render "stuck" in a corner.
@Component({
  selector: 'app-custom-cursor',
  standalone: true,
  templateUrl: './custom-cursor.component.html',
  styleUrl: './custom-cursor.component.css'
})
export class CustomCursorComponent implements AfterViewInit, OnDestroy {
  @ViewChild('dot', { static: true }) dotRef!: ElementRef<HTMLElement>;
  @ViewChild('ring', { static: true }) ringRef!: ElementRef<HTMLElement>;

  private moveRingX?: (v: number) => void;
  private moveRingY?: (v: number) => void;
  private armed = false;
  private readonly onMove = (e: MouseEvent) => this.handleMove(e);
  private readonly onDown = () => this.setPressed(true);
  private readonly onUp = () => this.setPressed(false);
  private readonly onOver = (e: MouseEvent) => this.handleOver(e);

  ngAfterViewInit(): void {
    document.body.classList.add('has-custom-cursor');

    gsap.set([this.dotRef.nativeElement, this.ringRef.nativeElement], { xPercent: -50, yPercent: -50 });

    this.moveRingX = gsap.quickTo(this.ringRef.nativeElement, 'x', { duration: 0.18, ease: 'power3.out' });
    this.moveRingY = gsap.quickTo(this.ringRef.nativeElement, 'y', { duration: 0.18, ease: 'power3.out' });

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
    }
    gsap.set(this.dotRef.nativeElement, { x: e.clientX, y: e.clientY });
    this.moveRingX?.(e.clientX);
    this.moveRingY?.(e.clientY);
  }

  private setPressed(pressed: boolean): void {
    this.ringRef.nativeElement.classList.toggle('pressed', pressed);
  }

  private handleOver(e: MouseEvent): void {
    const interactive = (e.target as HTMLElement)?.closest(
      'button, a, input, select, [role="button"], .magnetic, .env-tab, .tabs button'
    );
    this.ringRef.nativeElement.classList.toggle('hover', !!interactive);
  }
}
