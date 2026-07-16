import { Injectable, signal } from '@angular/core';

// Orchestrates the cinematic login → dashboard sequence. When login succeeds
// AppComponent flips this signal on; a fullscreen JarvisIntroComponent overlays
// the app, plays a ~2.4s "globe expands, then contracts into position" scene,
// then flips the signal off and the dashboard is revealed with the iris view
// transition already wired to the viewport.
@Injectable({ providedIn: 'root' })
export class LoginIntroService {
  readonly playing = signal<boolean>(false);

  play(durationMs = 2400): Promise<void> {
    this.playing.set(true);
    return new Promise((resolve) => {
      setTimeout(() => {
        this.playing.set(false);
        resolve();
      }, durationMs);
    });
  }
}
