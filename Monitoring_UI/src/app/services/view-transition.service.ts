import { Injectable } from '@angular/core';
import gsap from 'gsap';

// Gives switching between nav tabs the feel of one continuous, smooth-scrolling
// site rather than a hard page swap — the freshly-mounted view rises and
// fades in as a whole.
@Injectable({ providedIn: 'root' })
export class ViewTransitionService {
  private readonly reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  enter(container: HTMLElement): void {
    const child = container.firstElementChild as HTMLElement | null;
    if (!child) return;
    // GSAP animates y via an inline `transform`, and never removing it leaves
    // a stray `transform: translate3d(0,0,0)` on the view's root element even
    // once settled at y:0. Any transform on an ancestor creates a new CSS
    // containing block for `position: fixed` descendants — so a modal inside
    // this view would center itself relative to this element instead of the
    // viewport. clearProps drops the inline transform once the animation
    // finishes settling on the identity transform, with no visual difference.
    if (this.reducedMotion) {
      gsap.set(child, { opacity: 1, y: 0, clearProps: 'transform' });
      return;
    }
    gsap.fromTo(
      child,
      { opacity: 0, y: 18 },
      { opacity: 1, y: 0, duration: 0.5, ease: 'power3.out', clearProps: 'transform' }
    );
  }
}
