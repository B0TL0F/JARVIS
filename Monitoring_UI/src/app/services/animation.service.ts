import { Injectable } from '@angular/core';
import gsap from 'gsap';
import { ScrollTrigger } from 'gsap/ScrollTrigger';

gsap.registerPlugin(ScrollTrigger);

// Central place for the app's motion language: purposeful, restrained reveals
// with a consistent easing/timing feel across every view — modeled on the
// "every movement earns its place" philosophy rather than decorative motion.
@Injectable({ providedIn: 'root' })
export class AnimationService {
  private readonly reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  // Fades/rises every `.gsap-reveal` child of `container` in as a group.
  revealIn(container: HTMLElement, delay = 0): void {
    const els = container.querySelectorAll<HTMLElement>('.gsap-reveal');
    if (!els.length) return;
    if (this.reducedMotion) {
      gsap.set(els, { opacity: 1, y: 0 });
      return;
    }
    gsap.fromTo(
      els,
      { opacity: 0, y: 16 },
      { opacity: 1, y: 0, duration: 0.6, ease: 'power3.out', stagger: 0.06, delay }
    );
  }

  // Splits a headline into words and reveals them with a rising mask effect —
  // a lightweight stand-in for GSAP's paid SplitText plugin.
  revealHeadline(el: HTMLElement | undefined | null): void {
    if (!el) return;
    const text = el.textContent?.trim() ?? '';
    if (!text) return;
    const words = text.split(/\s+/);
    el.innerHTML = words
      .map((w) => `<span class="gsap-word"><span class="gsap-word-inner">${w}</span></span>`)
      .join(' ');
    const inners = el.querySelectorAll<HTMLElement>('.gsap-word-inner');
    if (this.reducedMotion) {
      gsap.set(inners, { yPercent: 0, opacity: 1 });
      return;
    }
    gsap.fromTo(
      inners,
      { yPercent: 110, opacity: 0 },
      { yPercent: 0, opacity: 1, duration: 0.7, ease: 'expo.out', stagger: 0.045 }
    );
  }

  // Staggers every matching element in immediately, regardless of scroll
  // position. Use this for functional/data content (e.g. a status table) —
  // nothing should ever be invisible-until-scrolled on an ops dashboard,
  // unlike a portfolio page where progressive reveal is the point.
  staggerIn(container: HTMLElement, selector: string): void {
    const els = Array.from(container.querySelectorAll<HTMLElement>(selector));
    if (!els.length) return;
    if (this.reducedMotion) {
      gsap.set(els, { opacity: 1, y: 0 });
      return;
    }
    gsap.fromTo(
      els,
      { opacity: 0, y: 10 },
      { opacity: 1, y: 0, duration: 0.45, ease: 'power2.out', stagger: Math.min(0.02, 1 / els.length) }
    );
  }

  // Reveals repeated elements (rows/cards) as they scroll into view, once each.
  // Only appropriate for exploratory/browse content (pipeline cards, target
  // groups) where partial-below-the-fold content is expected, never for data
  // the user needs to see in full without scrolling.
  scrollReveal(container: HTMLElement, selector: string): void {
    const els = Array.from(container.querySelectorAll<HTMLElement>(selector));
    if (!els.length) return;
    if (this.reducedMotion) {
      gsap.set(els, { opacity: 1, y: 0 });
      return;
    }
    gsap.set(els, { opacity: 0, y: 14 });
    ScrollTrigger.batch(els, {
      start: 'top 94%',
      once: true,
      onEnter: (batch) => gsap.to(batch, { opacity: 1, y: 0, duration: 0.5, ease: 'power2.out', stagger: 0.04 })
    });
  }

  // Animates a number counting up to `value`, writing the formatted text into el.
  countUp(el: HTMLElement | undefined | null, value: number, suffix = ''): void {
    if (!el) return;
    if (this.reducedMotion) {
      el.textContent = `${value}${suffix}`;
      return;
    }
    const state = { val: Number(el.textContent) || 0 };
    gsap.to(state, {
      val: value,
      duration: 0.8,
      ease: 'power2.out',
      onUpdate: () => (el.textContent = `${Math.round(state.val)}${suffix}`)
    });
  }

  refresh(): void {
    ScrollTrigger.refresh();
  }
}
