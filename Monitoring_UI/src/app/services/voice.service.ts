import { Injectable } from '@angular/core';

// Wraps the browser's SpeechSynthesis API to give JARVIS an actual voice.
// Chooses the best-available English male voice (British preferred — that's
// the closest thing browsers ship to the movie JARVIS). Falls back silently
// if speech synthesis is unavailable, disabled, or the user has muted it.
// All speech is fire-and-forget; we never block the UI on it.
@Injectable({ providedIn: 'root' })
export class VoiceService {
  private cachedVoice: SpeechSynthesisVoice | null = null;
  private voicesReady: Promise<void>;
  private muted = false;

  // Prefer these voices in order — British male voices give the strongest
  // JARVIS vibe. "Google UK English Male" is the crown jewel on Chrome.
  // Everything else is a graceful fallback.
  private readonly voicePreference = [
    'Google UK English Male',
    'Microsoft Ryan Online',
    'Microsoft George',
    'Microsoft George - English (United Kingdom)',
    'Daniel',                // macOS / iOS UK male
    'Oliver',                // macOS UK male
    'Arthur',                // macOS UK male
    'Rishi',                 // macOS Indian English male
    'Google US English',     // last resort (usually female-sounding)
  ];

  constructor() {
    // Read the user's preference from localStorage so they can silence JARVIS
    // permanently if they want. Toggled via VoiceService.setMuted().
    this.muted = typeof localStorage !== 'undefined'
      && localStorage.getItem('jarvis.voice.muted') === '1';

    this.voicesReady = new Promise((resolve) => {
      if (typeof window === 'undefined' || !('speechSynthesis' in window)) {
        resolve();
        return;
      }
      const load = () => {
        const voices = speechSynthesis.getVoices();
        if (voices.length > 0) {
          this.cachedVoice = this.pickBestVoice(voices);
          resolve();
          return true;
        }
        return false;
      };
      // Voices may already be loaded, or arrive async via onvoiceschanged.
      if (!load()) {
        speechSynthesis.onvoiceschanged = () => {
          if (load()) speechSynthesis.onvoiceschanged = null;
        };
        // Safety net if onvoiceschanged never fires.
        setTimeout(load, 800);
      }
    });
  }

  isSupported(): boolean {
    return typeof window !== 'undefined' && 'speechSynthesis' in window;
  }

  isMuted(): boolean {
    return this.muted;
  }

  setMuted(muted: boolean): void {
    this.muted = muted;
    if (typeof localStorage !== 'undefined') {
      localStorage.setItem('jarvis.voice.muted', muted ? '1' : '0');
    }
    if (muted && this.isSupported()) speechSynthesis.cancel();
  }

  // Fire-and-forget speech. `rate`/`pitch` defaults are tuned to sound
  // measured and slightly authoritative rather than perky.
  async speak(text: string, opts: { rate?: number; pitch?: number; volume?: number } = {}): Promise<void> {
    if (!this.isSupported() || this.muted) return;
    await this.voicesReady;
    try {
      speechSynthesis.cancel(); // never overlap voice lines
      const utter = new SpeechSynthesisUtterance(text);
      if (this.cachedVoice) utter.voice = this.cachedVoice;
      utter.rate = opts.rate ?? 0.94;
      utter.pitch = opts.pitch ?? 0.85;
      utter.volume = opts.volume ?? 1;
      utter.lang = this.cachedVoice?.lang ?? 'en-GB';
      speechSynthesis.speak(utter);
    } catch {
      // Never let a speech-synthesis edge case bubble into the UI flow.
    }
  }

  private pickBestVoice(voices: SpeechSynthesisVoice[]): SpeechSynthesisVoice | null {
    // 1) exact name matches from preference list.
    for (const name of this.voicePreference) {
      const hit = voices.find((v) => v.name === name);
      if (hit) return hit;
    }
    // 2) any en-GB voice.
    const gb = voices.find((v) => v.lang && v.lang.toLowerCase().startsWith('en-gb'));
    if (gb) return gb;
    // 3) any English voice that mentions "male" in the name.
    const enMale = voices.find(
      (v) => v.lang.toLowerCase().startsWith('en') && /male/i.test(v.name) && !/female/i.test(v.name)
    );
    if (enMale) return enMale;
    // 4) any English voice.
    const en = voices.find((v) => v.lang.toLowerCase().startsWith('en'));
    if (en) return en;
    // 5) whatever the browser has.
    return voices[0] ?? null;
  }
}
