import { Directive, ElementRef, HostListener, OnDestroy } from '@angular/core';
import gsap from 'gsap';

// A subtle cursor-attraction effect for buttons/tabs — the hallmark
// micro-interaction of premium interactive portfolios. Nudges the element
// toward the pointer on hover, springs back on leave. Disabled for
// reduced-motion users (the button still works, it just doesn't move).
@Directive({
  selector: '.magnetic',
  standalone: true
})
export class MagneticDirective implements OnDestroy {
  private readonly reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  private quickX?: (v: number) => void;
  private quickY?: (v: number) => void;

  constructor(private el: ElementRef<HTMLElement>) {
    if (!this.reducedMotion) {
      this.quickX = gsap.quickTo(this.el.nativeElement, 'x', { duration: 0.35, ease: 'power3.out' });
      this.quickY = gsap.quickTo(this.el.nativeElement, 'y', { duration: 0.35, ease: 'power3.out' });
    }
  }

  @HostListener('mousemove', ['$event'])
  onMouseMove(e: MouseEvent): void {
    if (!this.quickX || !this.quickY) return;
    const rect = this.el.nativeElement.getBoundingClientRect();
    this.quickX((e.clientX - rect.left - rect.width / 2) * 0.28);
    this.quickY((e.clientY - rect.top - rect.height / 2) * 0.35);
  }

  @HostListener('mouseleave')
  onMouseLeave(): void {
    this.quickX?.(0);
    this.quickY?.(0);
  }

  ngOnDestroy(): void {
    gsap.killTweensOf(this.el.nativeElement);
  }
}
