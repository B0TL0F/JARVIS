import { Injectable } from '@angular/core';
import gsap from 'gsap';

// Cinematic view transitions. On every route/tab swap:
//   1. Trigger a full-screen HUD "iris" wipe overlay (red scanline sweep with
//      corner-brackets) that plays over ~500ms.
//   2. Simultaneously fade + slide the newly-mounted view in with a slight
//      3D tilt, so it feels like a HUD panel opening up rather than a hard
//      swap. Includes staggered reveal for direct grid/panel children.
@Injectable({ providedIn: 'root' })
export class ViewTransitionService {
  private readonly reducedMotion =
    typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  enter(container: HTMLElement): void {
    const child = container.firstElementChild as HTMLElement | null;
    if (!child) return;

    if (this.reducedMotion) {
      gsap.set(child, { opacity: 1, y: 0, clearProps: 'transform' });
      this.staggerImmediate(child);
      return;
    }

    // Iris wipe overlay — a full-screen swipe that reveals the new view.
    this.playIris();

    // The newly-mounted view: fade + slide + slight 3D flip in.
    gsap.fromTo(
      child,
      { opacity: 0, y: 22, rotateX: 4, transformPerspective: 900 },
      {
        opacity: 1,
        y: 0,
        rotateX: 0,
        duration: 0.62,
        ease: 'power3.out',
        delay: 0.08,
        clearProps: 'transform,transformPerspective'
      }
    );

    // Staggered reveal of direct children (cards / rows / hud tiles).
    const grandChildren = Array.from(child.children).filter(
      (el): el is HTMLElement => el instanceof HTMLElement
    );
    if (grandChildren.length > 1) {
      gsap.fromTo(
        grandChildren,
        { opacity: 0, y: 14 },
        {
          opacity: 1,
          y: 0,
          duration: 0.48,
          ease: 'power2.out',
          stagger: 0.055,
          delay: 0.15,
          clearProps: 'transform'
        }
      );
    }
  }

  private staggerImmediate(child: HTMLElement): void {
    const grandChildren = Array.from(child.children).filter(
      (el): el is HTMLElement => el instanceof HTMLElement
    );
    gsap.set(grandChildren, { opacity: 1, y: 0 });
  }

  private playIris(): void {
    let overlay = document.getElementById('jv-iris-overlay');
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'jv-iris-overlay';
      overlay.innerHTML = `
        <div class="iris-scan"></div>
        <span class="iris-corner tl"></span>
        <span class="iris-corner tr"></span>
        <span class="iris-corner bl"></span>
        <span class="iris-corner br"></span>
        <span class="iris-label">// SWITCHING PANEL...</span>
      `;
      document.body.appendChild(overlay);
    }
    // Restart animation by toggling the class.
    overlay.classList.remove('run');
    // Force reflow so removing/adding the class re-triggers the CSS animation.
    void overlay.offsetWidth;
    overlay.classList.add('run');
  }
}
