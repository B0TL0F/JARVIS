import { AfterViewInit, Component, ElementRef, OnDestroy, ViewChild } from '@angular/core';
import gsap from 'gsap';

// A simple glowing dot cursor that snaps to the pointer and grows slightly
// on hover over interactive elements. Starts hidden until the first real
// mousemove so it never renders "stuck" in a corner if JS is slow to attach.
@Component({
  selector: 'app-custom-cursor',
  standalone: true,
  templateUrl: './custom-cursor.component.html',
  styleUrl: './custom-cursor.component.css'
})
export class CustomCursorComponent implements AfterViewInit, OnDestroy {
  @ViewChild('dot', { static: true }) dotRef!: ElementRef<HTMLElement>;

  private armed = false;
  private readonly onMove = (e: MouseEvent) => this.handleMove(e);
  private readonly onDown = () => this.setPressed(true);
  private readonly onUp = () => this.setPressed(false);
  private readonly onOver = (e: MouseEvent) => this.handleOver(e);

  ngAfterViewInit(): void {
    document.body.classList.add('has-custom-cursor');

    gsap.set(this.dotRef.nativeElement, { xPercent: -50, yPercent: -50 });

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
    }
    gsap.set(this.dotRef.nativeElement, { x: e.clientX, y: e.clientY });
  }

  private setPressed(pressed: boolean): void {
    this.dotRef.nativeElement.classList.toggle('pressed', pressed);
  }

  private handleOver(e: MouseEvent): void {
    const interactive = (e.target as HTMLElement)?.closest(
      'button, a, input, select, [role="button"], .magnetic, .env-tab, .tabs button, .navtab, .header-btn'
    );
    this.dotRef.nativeElement.classList.toggle('hover', !!interactive);
  }
}
