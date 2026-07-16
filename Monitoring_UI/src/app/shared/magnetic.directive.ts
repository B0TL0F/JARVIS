import { Directive, ElementRef, HostListener, OnDestroy } from '@angular/core';
import gsap from 'gsap';

// A cinematic cursor-attraction effect for buttons/tabs. Nudges the element
// toward the pointer, tilts it slightly on 3D X/Y, and gently glows the
// accent-soft shadow while hovered. Springs back on leave. Disabled for
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
  private quickRotX?: (v: number) => void;
  private quickRotY?: (v: number) => void;

  constructor(private el: ElementRef<HTMLElement>) {
    if (!this.reducedMotion) {
      const target = this.el.nativeElement;
      // Establish a perspective so rotateX/Y actually depth-tilt.
      gsap.set(target, { transformPerspective: 700 });
      this.quickX = gsap.quickTo(target, 'x', { duration: 0.32, ease: 'power3.out' });
      this.quickY = gsap.quickTo(target, 'y', { duration: 0.32, ease: 'power3.out' });
      this.quickRotX = gsap.quickTo(target, 'rotateX', { duration: 0.32, ease: 'power3.out' });
      this.quickRotY = gsap.quickTo(target, 'rotateY', { duration: 0.32, ease: 'power3.out' });
    }
  }

  @HostListener('mousemove', ['$event'])
  onMouseMove(e: MouseEvent): void {
    if (!this.quickX || !this.quickY) return;
    const rect = this.el.nativeElement.getBoundingClientRect();
    const dx = (e.clientX - rect.left - rect.width / 2);
    const dy = (e.clientY - rect.top - rect.height / 2);
    this.quickX(dx * 0.32);
    this.quickY(dy * 0.4);
    this.quickRotY?.(dx * 0.06);
    this.quickRotX?.(-dy * 0.08);
  }

  @HostListener('mouseleave')
  onMouseLeave(): void {
    this.quickX?.(0);
    this.quickY?.(0);
    this.quickRotX?.(0);
    this.quickRotY?.(0);
  }

  ngOnDestroy(): void {
    gsap.killTweensOf(this.el.nativeElement);
  }
}
