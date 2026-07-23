import { Injectable } from '@angular/core';
import { BehaviorSubject, Subject } from 'rxjs';

// Minimal ambient typings for the browser Web Speech API (not in default TS DOM lib).
interface SpeechRecognitionResultLike {
  isFinal: boolean;
  [index: number]: { transcript: string };
}
interface SpeechRecognitionEventLike extends Event {
  resultIndex: number;
  results: { length: number; [index: number]: SpeechRecognitionResultLike };
}
interface SpeechRecognitionErrorEventLike extends Event {
  error: string; // e.g. 'not-allowed' | 'no-speech' | 'network' | 'audio-capture' | 'service-not-allowed'
  message?: string;
}
interface SpeechRecognitionLike extends EventTarget {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  start(): void;
  stop(): void;
  onresult: ((event: SpeechRecognitionEventLike) => void) | null;
  onerror: ((event: SpeechRecognitionErrorEventLike) => void) | null;
  onend: (() => void) | null;
}

// Continuous voice conversation with Jarvis, modeled on the reference implementation in
// Theme/agentic-os-personal/content-os/public/hud/hud.js: tap to start a conversation turn,
// capture speech with live mic-level metering (drives the globe "beat"), hand the transcript
// to the caller, and — once the caller has spoken the reply back — immediately resume
// listening for the next turn without needing the wake word again. A conversation ends on a
// stop phrase, an explicit stop, or enough silence.
//
// On top of that base loop, this also supports optional hands-free activation via "Hey
// Jarvis" wake-word detection (continuous Web Speech API recognition, restarted on every
// auto-stop) — entirely client-side; browsers don't expose a dedicated always-on wake-word
// API, so continuous SpeechRecognition listening for the phrase is the standard approach.
// Note: SpeechRecognition depends on a browser-provided network speech service — Brave (and
// Chromium builds without Google's proprietary API key) do not have access to it and will
// always fail with a "network" error; Chrome/Edge work.
@Injectable({ providedIn: 'root' })
export class VoiceActivityService {
  private readonly _listening = new BehaviorSubject<boolean>(false);
  private readonly _voiceLevel = new BehaviorSubject<number>(0);
  private readonly _enabled = new BehaviorSubject<boolean>(false); // wake-word armed
  private readonly _conversing = new BehaviorSubject<boolean>(false); // actively in a voice turn/conversation
  private readonly _command = new Subject<string>();
  private readonly _diagnostic = new BehaviorSubject<string | null>(null);

  readonly listening$ = this._listening.asObservable();
  readonly voiceLevel$ = this._voiceLevel.asObservable();
  readonly enabled$ = this._enabled.asObservable();
  readonly conversing$ = this._conversing.asObservable();
  readonly command$ = this._command.asObservable();
  // Last recognition/mic error, surfaced to the UI so failures aren't silent — this API fails
  // in ways that are otherwise invisible (e.g. Brave Shields blocking the network speech
  // recognition backend).
  readonly diagnostic$ = this._diagnostic.asObservable();

  private static readonly WAKE_PHRASES = ['hey jarvis', 'hey, jarvis', 'ok jarvis', 'okay jarvis', 'hey  jarvis'];
  private static readonly COMMAND_SILENCE_TIMEOUT_MS = 8000;

  private wakeRecognition: SpeechRecognitionLike | null = null;
  private commandRecognition: SpeechRecognitionLike | null = null;
  private audioCtx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private micStream: MediaStream | null = null;
  private rafId: number | null = null;
  private commandTimeoutId: ReturnType<typeof setTimeout> | null = null;
  // Chromium's continuous SpeechRecognition is known to silently stop delivering
  // results/events without ever firing onend — with no visible symptom other than the
  // wake word just stopping forever. This watchdog periodically force-restarts wake
  // listening if it's been "running" longer than any real recognizer session should need.
  private static readonly WATCHDOG_INTERVAL_MS = 20000;
  private watchdogId: ReturnType<typeof setInterval> | null = null;
  private wakeRecognitionStartedAt = 0;
  // Set right before a command-capture recognizer successfully returns a transcript, so its
  // `onend` (which always fires right after `onresult`) knows to leave the mic/metering running
  // for the caller's continueConversation() call instead of tearing down and resuming wake-word
  // listening — that's only correct for the timeout/no-speech/error paths.
  private awaitingCallerDecision = false;

  get supported(): boolean {
    const w = window as unknown as { SpeechRecognition?: unknown; webkitSpeechRecognition?: unknown };
    return typeof window !== 'undefined' && !!(w.SpeechRecognition || w.webkitSpeechRecognition);
  }

  get isEnabled(): boolean {
    return this._enabled.value;
  }

  get isConversing(): boolean {
    return this._conversing.value;
  }

  // Turns a raw SpeechRecognition error code into an actionable message. 'network' is the
  // signature error of privacy-hardened Chromium builds (Brave, and Chromium built without
  // Google's proprietary API key) that lack the credentials the browser's built-in speech
  // service needs — it is not something this app can work around; only a different browser
  // (Chrome/Edge) fixes it. Firefox/Safari don't support continuous recognition at all.
  private describeError(code: string): string {
    switch (code) {
      case 'network':
        return 'Speech recognition failed with a "network" error — this usually means the browser (e.g. Brave) ' +
               'blocks or lacks access to the built-in speech-recognition service. Try Google Chrome or Microsoft Edge instead.';
      case 'not-allowed':
      case 'service-not-allowed':
        return 'Microphone access was blocked. Check the browser\'s site permissions and allow microphone access for this page.';
      case 'audio-capture':
        return 'No microphone was found. Check that a mic is connected and not in use by another app.';
      default:
        return `Voice recognition error: ${code}`;
    }
  }

  // Arms hands-free "Hey Jarvis" wake-word listening. Must be called from a user-gesture
  // handler (button click) the first time, since browsers require that for microphone
  // permission prompts.
  enable(): void {
    if (!this.supported || this._enabled.value) return;
    this._diagnostic.next(null);
    this._enabled.next(true);
    this.startWakeListening();
    this.startWatchdog();
  }

  disable(): void {
    this._enabled.next(false);
    this.stopWatchdog();
    this.stopWakeListening();
    this.stopConversation();
  }

  // Tap-to-talk entry point (e.g. clicking the globe): skips the wake word and starts
  // listening for a command immediately. Must be called from a user-gesture handler the first
  // time, for the same permission reason as `enable()`.
  startConversationNow(): void {
    if (!this.supported) return;
    this._diagnostic.next(null);
    this._enabled.next(true);
    this.stopWakeListening(); // only one recognizer active at a time
    this._conversing.next(true);
    this.beginCommandCapture();
  }

  // Called by the chat UI once it has finished speaking the reply back, to keep the
  // conversation going without requiring the wake word again. No-ops if the conversation was
  // already ended (stop phrase / manual stop / timeout) while the reply was being spoken.
  continueConversation(): void {
    if (!this._enabled.value || !this._conversing.value) return;
    this.beginCommandCapture();
  }

  // Ends the current conversation (if any) and returns to wake-word listening (if still armed)
  // or fully idle (if wake word was never armed — e.g. conversation started via tap-to-talk).
  stopConversation(): void {
    this.awaitingCallerDecision = false;
    this._conversing.next(false);
    this.teardownCommandRecognition();
    this.teardownMetering();
    this._listening.next(false);
    this._voiceLevel.next(0);

    if (this._enabled.value) {
      // Small delay avoids immediately re-triggering wake-word detection on trailing audio.
      setTimeout(() => this.startWakeListening(), 300);
    }
  }

  private getRecognitionCtor(): (new () => SpeechRecognitionLike) | null {
    const w = window as unknown as {
      SpeechRecognition?: new () => SpeechRecognitionLike;
      webkitSpeechRecognition?: new () => SpeechRecognitionLike;
    };
    return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null;
  }

  private startWakeListening(): void {
    const Ctor = this.getRecognitionCtor();
    if (!Ctor) return;

    const rec = new Ctor();
    rec.lang = 'en-US';
    rec.continuous = true;
    rec.interimResults = true;

    rec.onresult = (event: SpeechRecognitionEventLike) => {
      let transcript = '';
      for (let i = event.resultIndex; i < event.results.length; i++) {
        transcript += event.results[i][0].transcript;
      }
      const lower = transcript.toLowerCase();
      if (!this._conversing.value && VoiceActivityService.WAKE_PHRASES.some((p) => lower.includes(p))) {
        this.onWakeWordDetected();
      }
    };
    rec.onerror = (event: SpeechRecognitionErrorEventLike) => {
      // 'no-speech' and transient 'aborted' blips are common with always-on recognition and
      // self-heal via onend restarting — only surface anything persistent-looking.
      if (event.error !== 'no-speech' && event.error !== 'aborted') {
        console.warn('[VoiceActivityService] wake-word recognition error:', event.error, event.message ?? '');
        this._diagnostic.next(this.describeError(event.error));
      }
    };
    rec.onend = () => {
      // Browsers stop continuous recognition after a period of silence — restart it as long
      // as the feature is still enabled and we're not mid-conversation. A failed restart here
      // is surfaced (not swallowed) so a dead wake-word listener is visible instead of just
      // silently never triggering again.
      if (this._enabled.value && !this._conversing.value) {
        this.restartWakeRecognition(rec, 'onend');
      }
    };

    this.wakeRecognition = rec;
    this.wakeRecognitionStartedAt = Date.now();
    try {
      rec.start();
    } catch (err) {
      console.warn('[VoiceActivityService] wake-word start() failed:', err);
      this._diagnostic.next('"Hey Jarvis" listening failed to start — retrying…');
      // Most likely "already started" (harmless) or a transient failure — retry shortly
      // instead of leaving wake listening permanently dead with no visible sign.
      setTimeout(() => {
        if (this._enabled.value && !this._conversing.value) this.startWakeListening();
      }, 2000);
    }
  }

  // Attempts an in-place restart first (cheapest, matches prior behavior); if that throws,
  // tears down and builds a fresh recognizer — some Chromium failure modes leave the old
  // instance permanently unusable even though it still exists.
  private restartWakeRecognition(rec: SpeechRecognitionLike, reason: string): void {
    try {
      rec.start();
      this.wakeRecognitionStartedAt = Date.now();
    } catch (err) {
      console.warn(`[VoiceActivityService] wake-word restart (${reason}) failed, rebuilding recognizer:`, err);
      this.wakeRecognition = null;
      setTimeout(() => {
        if (this._enabled.value && !this._conversing.value) this.startWakeListening();
      }, 1000);
    }
  }

  // Periodically verifies wake listening hasn't silently died (Chromium's continuous
  // SpeechRecognition is known to stop delivering results without ever firing onend). If the
  // current recognizer instance has been "running" far longer than any real session should,
  // force a stop+restart cycle.
  private startWatchdog(): void {
    this.stopWatchdog();
    this.watchdogId = setInterval(() => {
      if (!this._enabled.value || this._conversing.value || !this.wakeRecognition) return;
      const stale = Date.now() - this.wakeRecognitionStartedAt > VoiceActivityService.WATCHDOG_INTERVAL_MS * 3;
      if (stale) {
        console.warn('[VoiceActivityService] wake-word watchdog: forcing restart (no activity for too long)');
        this.stopWakeListening();
        this.startWakeListening();
      }
    }, VoiceActivityService.WATCHDOG_INTERVAL_MS);
  }

  private stopWatchdog(): void {
    if (this.watchdogId) {
      clearInterval(this.watchdogId);
      this.watchdogId = null;
    }
  }

  private stopWakeListening(): void {
    this.wakeRecognition?.stop();
    this.wakeRecognition = null;
  }

  private onWakeWordDetected(): void {
    this._conversing.next(true);
    this.stopWakeListening();
    this.beginCommandCapture();
  }

  // Starts (or, on a continuation turn, keeps using) mic-level metering and one command-capture
  // recognition pass. Safe to call repeatedly across a multi-turn conversation.
  private beginCommandCapture(): void {
    this._listening.next(true);
    if (!this.micStream) {
      this.startVolumeMetering();
    }
    this.startCommandCapture();
  }

  private async startVolumeMetering(): Promise<void> {
    try {
      this.micStream = await navigator.mediaDevices.getUserMedia({ audio: true });
      const AudioCtx = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext;
      this.audioCtx = new AudioCtx();
      const source = this.audioCtx.createMediaStreamSource(this.micStream);
      this.analyser = this.audioCtx.createAnalyser();
      this.analyser.fftSize = 512;
      this.analyser.smoothingTimeConstant = 0.6;
      source.connect(this.analyser);

      const data = new Uint8Array(this.analyser.frequencyBinCount);
      const loop = () => {
        if (!this.analyser) return;
        this.analyser.getByteTimeDomainData(data);
        let sumSquares = 0;
        for (let i = 0; i < data.length; i++) {
          const v = (data[i] - 128) / 128;
          sumSquares += v * v;
        }
        const rms = Math.sqrt(sumSquares / data.length);
        // Typical speech RMS on this scale is small — boost and clamp to a usable 0..1 range
        // for the UI "beat" animation. Below ~0.03 reads as silence (steady glow, no beat).
        const level = Math.min(1, rms * 5.5);
        this._voiceLevel.next(level);
        this.rafId = requestAnimationFrame(loop);
      };
      loop();
    } catch (err) {
      // Mic permission denied/unavailable — command recognition still works via the Speech
      // Recognition API's own mic access; we just lose the volume-driven "beat" visual.
      console.warn('[VoiceActivityService] mic volume metering unavailable:', err);
      this._diagnostic.next('Mic volume metering unavailable — listening still works, but the globe will only glow, not beat.');
    }
  }

  private startCommandCapture(): void {
    const Ctor = this.getRecognitionCtor();
    if (!Ctor) {
      this.stopConversation();
      return;
    }

    const rec = new Ctor();
    rec.lang = 'en-US';
    rec.continuous = false;
    rec.interimResults = false;

    rec.onresult = (event: SpeechRecognitionEventLike) => {
      const transcript = event.results[0]?.[0]?.transcript?.trim();
      if (transcript) {
        this.awaitingCallerDecision = true;
        this._command.next(transcript);
      }
    };
    rec.onerror = (event: SpeechRecognitionErrorEventLike) => {
      if (event.error !== 'no-speech' && event.error !== 'aborted') {
        console.warn('[VoiceActivityService] command capture error:', event.error, event.message ?? '');
        this._diagnostic.next(this.describeError(event.error));
      }
    };
    rec.onend = () => {
      if (this.awaitingCallerDecision) {
        // A transcript was captured — the caller (ChatComponent) now owns what happens next:
        // it will speak the reply then call continueConversation(), or detect a stop phrase
        // and call stopConversation(). Just stop this one-shot recognizer; leave the mic
        // stream/metering running since another turn is likely coming.
        this.awaitingCallerDecision = false;
        this.commandRecognition = null;
        if (this.commandTimeoutId) {
          clearTimeout(this.commandTimeoutId);
          this.commandTimeoutId = null;
        }
        return;
      }
      // No transcript (timeout/error/silence) — end the conversation and return to wake-word
      // listening (or fully idle).
      this.stopConversation();
    };

    this.commandRecognition = rec;
    this.commandTimeoutId = setTimeout(() => {
      this.commandRecognition?.stop();
    }, VoiceActivityService.COMMAND_SILENCE_TIMEOUT_MS);
    try {
      rec.start();
    } catch {
      this.stopConversation();
    }
  }

  private teardownCommandRecognition(): void {
    if (this.commandTimeoutId) {
      clearTimeout(this.commandTimeoutId);
      this.commandTimeoutId = null;
    }
    this.commandRecognition?.stop();
    this.commandRecognition = null;
  }

  private teardownMetering(): void {
    if (this.rafId) cancelAnimationFrame(this.rafId);
    this.rafId = null;
    this.analyser = null;
    this.micStream?.getTracks().forEach((t) => t.stop());
    this.micStream = null;
    this.audioCtx?.close().catch(() => {});
    this.audioCtx = null;
  }
}
