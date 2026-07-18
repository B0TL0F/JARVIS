import { Component, ElementRef, Input, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { ChatService, PendingAction } from '../services/chat.service';
import { StatusService } from '../services/status.service';
import { VoiceActivityService } from '../services/voice-activity.service';
import { VoiceService } from '../services/voice.service';

interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
  configured?: boolean;
  pendingAction?: PendingAction | null;
  actionResolved?: 'confirmed' | 'cancelled';
}

// Minimal ambient typings for the browser Web Speech API (not in default TS DOM lib).
interface SpeechRecognitionResultLike {
  isFinal: boolean;
  [index: number]: { transcript: string };
}
interface SpeechRecognitionEventLike extends Event {
  resultIndex: number;
  results: { length: number; [index: number]: SpeechRecognitionResultLike };
}
interface SpeechRecognitionLike extends EventTarget {
  lang: string;
  continuous: boolean;
  interimResults: boolean;
  start(): void;
  stop(): void;
  onresult: ((event: SpeechRecognitionEventLike) => void) | null;
  onerror: ((event: Event) => void) | null;
  onend: (() => void) | null;
}

// Phrases that end a voice conversation instead of being sent to the assistant — matches the
// reference implementation's stop-word handling (Theme/agentic-os-personal hud.js).
const STOP_PHRASES = /^\s*(stop|cancel|that'?s all|thank you,?\s*jarvis|thanks,?\s*jarvis)\.?\s*$/i;

// When a pending admin action is awaiting confirmation, these phrases confirm/cancel it BY
// VOICE instead of requiring a click on the Confirm/Cancel buttons — lets remediation-rule
// creation and other admin actions be driven end-to-end hands-free.
const CONFIRM_PHRASES = /^\s*(yes|yeah|yep|confirm|do it|go ahead|proceed)\.?\s*$/i;
const CANCEL_PHRASES = /^\s*(no|nope|cancel that|don'?t|never mind)\.?\s*$/i;

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.css'
})
export class ChatComponent implements OnInit, OnDestroy {
  // Embedded = the compact "IDLE / ^ CHAT HISTORY / input" console shown under the Dashboard
  // globe. Non-embedded = the full-page "Ask Jarvis" nav tab. Both share all chat/voice logic.
  @Input() embedded = false;

  messages: ChatMessage[] = [
    { role: 'assistant', text: 'Ask me about current module status, recent incidents, pipeline health — or, if you\'re an admin, tell me to trigger/cancel/delete/rename a pipeline, manage targets or users, or update alert settings. I\'ll always confirm before making any change.' }
  ];
  question = '';
  asking = false;
  triggering = false;
  selectedEnvironment: string | null = null;

  // Manual push-to-talk mic (existing button, full-page view only) — a single one-shot
  // question, separate from the continuous conversation loop below.
  voiceSupported = false;
  listening = false;

  // Continuous voice conversation (embedded console): tap the globe or the mic button to
  // start — or say "Hey Jarvis" if wake-word is armed — then keep talking back and forth
  // without repeating the wake word until a stop phrase, silence, or manual stop.
  wakeWordSupported = false;
  wakeWordEnabled = false;
  conversing = false;
  historyExpanded = false;
  voiceDiagnostic: string | null = null;

  @ViewChild('scrollAnchor') scrollAnchor?: ElementRef<HTMLElement>;

  private sub?: Subscription;
  private conversingSub?: Subscription;
  private commandSub?: Subscription;
  private diagnosticSub?: Subscription;
  private recognition: SpeechRecognitionLike | null = null;

  constructor(
    private chat: ChatService,
    private statusService: StatusService,
    private voiceActivity: VoiceActivityService,
    private voice: VoiceService
  ) {}

  ngOnInit(): void {
    this.sub = this.statusService.environment$.subscribe((env) => (this.selectedEnvironment = env));
    this.setupSpeechRecognition();

    this.wakeWordSupported = this.voiceActivity.supported;
    this.wakeWordEnabled = this.voiceActivity.isEnabled;
    this.conversingSub = this.voiceActivity.conversing$.subscribe((c) => (this.conversing = c));
    this.commandSub = this.voiceActivity.command$.subscribe((transcript) => this.onVoiceCommand(transcript));
    this.diagnosticSub = this.voiceActivity.diagnostic$.subscribe((d) => (this.voiceDiagnostic = d));

    // Arm "Hey Jarvis" hands-free the moment the Dashboard loads — no click needed. This calls
    // straight into SpeechRecognition.start(), which triggers the browser's own mic-permission
    // prompt on first use (that does NOT require a user gesture, unlike audio autoplay); once
    // granted, the browser remembers it for this origin and every later page load/login arms
    // silently. If the user denies it, the resulting 'not-allowed' error is surfaced via
    // voiceDiagnostic and they can still tap the globe/mic to talk manually.
    if (this.embedded && this.wakeWordSupported) {
      this.voiceActivity.enable();
      this.wakeWordEnabled = true;
    }
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
    this.conversingSub?.unsubscribe();
    this.commandSub?.unsubscribe();
    this.diagnosticSub?.unsubscribe();
    this.recognition?.stop();
  }

  get statusWord(): string {
    if (this.asking) return 'THINKING';
    if (this.conversing) return 'LISTENING';
    if (this.wakeWordEnabled) return 'WAITING FOR "HEY JARVIS"';
    return 'IDLE';
  }

  // Tap-to-talk: starts a continuous conversation immediately (no wake word needed). Tapping
  // again while already conversing ends it. This is the primary activation gesture — also
  // wired to a click on the Dashboard globe itself.
  toggleConversation(): void {
    if (this.voiceActivity.isConversing) {
      this.voiceActivity.stopConversation();
    } else {
      this.voiceActivity.startConversationNow();
    }
  }

  // Hands-free "Hey Jarvis" arm/disarm toggle (separate from tap-to-talk above).
  toggleWakeWord(): void {
    if (this.wakeWordEnabled) {
      this.voiceActivity.disable();
    } else {
      this.voiceActivity.enable();
    }
    this.wakeWordEnabled = this.voiceActivity.isEnabled;
  }

  toggleHistory(): void {
    this.historyExpanded = !this.historyExpanded;
  }

  // Latest assistant message still awaiting a Confirm/Cancel click, if any — walking backwards,
  // hitting a user message first means the most recent exchange had no pending action.
  private get pendingConfirmationMessage(): ChatMessage | undefined {
    for (let i = this.messages.length - 1; i >= 0; i--) {
      const m = this.messages[i];
      if (m.role === 'user') return undefined;
      if (m.pendingAction && !m.actionResolved) return m;
    }
    return undefined;
  }

  private onVoiceCommand(transcript: string): void {
    const pending = this.pendingConfirmationMessage;
    if (pending) {
      if (CONFIRM_PHRASES.test(transcript)) {
        this.confirmActionByVoice(pending);
        return;
      }
      if (CANCEL_PHRASES.test(transcript) || STOP_PHRASES.test(transcript)) {
        this.cancelActionByVoice(pending);
        return;
      }
      // Anything else while a confirmation is pending is treated as a new question — falls
      // through below — rather than silently discarding it.
    }

    if (STOP_PHRASES.test(transcript)) {
      this.voiceActivity.stopConversation();
      void this.voice.speak('Okay.');
      return;
    }
    this.question = transcript;
    this.askViaVoice();
  }

  // Voice-aware counterparts of confirmAction()/cancelAction() — same mutation logic, but also
  // speak the outcome and keep the conversation loop going afterward.
  private confirmActionByVoice(msg: ChatMessage): void {
    const action = msg.pendingAction;
    if (!action || this.triggering) return;

    this.triggering = true;
    this.chat.confirm(action).subscribe({
      next: async (res) => {
        msg.actionResolved = 'confirmed';
        this.messages.push({ role: 'assistant', text: res.message });
        this.triggering = false;
        this.scrollToBottom();
        await this.voice.speak(res.message);
        this.voiceActivity.continueConversation();
      },
      error: async () => {
        msg.actionResolved = 'confirmed';
        const text = 'Something went wrong performing that action.';
        this.messages.push({ role: 'assistant', text });
        this.triggering = false;
        this.scrollToBottom();
        await this.voice.speak(text);
        this.voiceActivity.continueConversation();
      }
    });
  }

  private cancelActionByVoice(msg: ChatMessage): void {
    msg.actionResolved = 'cancelled';
    const text = 'Okay, not doing that.';
    this.messages.push({ role: 'assistant', text });
    this.scrollToBottom();
    void this.voice.speak(text).then(() => this.voiceActivity.continueConversation());
  }

  private setupSpeechRecognition(): void {
    const w = window as unknown as {
      SpeechRecognition?: new () => SpeechRecognitionLike;
      webkitSpeechRecognition?: new () => SpeechRecognitionLike;
    };
    const SpeechRecognitionCtor = w.SpeechRecognition ?? w.webkitSpeechRecognition;
    if (!SpeechRecognitionCtor) {
      this.voiceSupported = false;
      return;
    }

    this.voiceSupported = true;
    const recognition = new SpeechRecognitionCtor();
    recognition.lang = 'en-US';
    recognition.continuous = false;
    recognition.interimResults = false;

    recognition.onresult = (event: SpeechRecognitionEventLike) => {
      let transcript = '';
      for (let i = event.resultIndex; i < event.results.length; i++) {
        if (event.results[i].isFinal) transcript += event.results[i][0].transcript;
      }
      if (transcript.trim()) {
        this.question = transcript.trim();
        this.ask();
      }
    };
    recognition.onerror = () => (this.listening = false);
    recognition.onend = () => (this.listening = false);

    this.recognition = recognition;
  }

  toggleListening(): void {
    if (!this.recognition) return;
    if (this.listening) {
      this.recognition.stop();
      this.listening = false;
      return;
    }
    this.listening = true;
    this.recognition.start();
  }

  ask(): void {
    const q = this.question.trim();
    if (!q || this.asking) return;

    this.messages.push({ role: 'user', text: q });
    this.question = '';
    this.asking = true;
    if (this.embedded) this.historyExpanded = true;

    this.chat.ask(q, this.selectedEnvironment ?? undefined).subscribe({
      next: (res) => {
        this.messages.push({
          role: 'assistant',
          text: res.answer,
          configured: res.claudeConfigured,
          pendingAction: res.pendingAction
        });
        this.asking = false;
        this.scrollToBottom();
      },
      error: () => {
        this.messages.push({ role: 'assistant', text: 'Something went wrong reaching the assistant.', configured: false });
        this.asking = false;
        this.scrollToBottom();
      }
    });

    setTimeout(() => this.scrollToBottom());
  }

  // Same as ask(), but speaks the reply back and keeps the conversation going afterward —
  // used for anything that came in via voice (wake word or tap-to-talk), never for typed
  // questions. Mirrors the reference implementation's listen → think → speak → listen loop.
  private askViaVoice(): void {
    const q = this.question.trim();
    if (!q || this.asking) return;

    this.messages.push({ role: 'user', text: q });
    this.question = '';
    this.asking = true;
    if (this.embedded) this.historyExpanded = true;

    this.chat.ask(q, this.selectedEnvironment ?? undefined).subscribe({
      next: async (res) => {
        this.messages.push({
          role: 'assistant',
          text: res.answer,
          configured: res.claudeConfigured,
          pendingAction: res.pendingAction
        });
        this.asking = false;
        this.scrollToBottom();
        await this.voice.speak(res.answer);
        this.voiceActivity.continueConversation();
      },
      error: async () => {
        this.messages.push({ role: 'assistant', text: 'Something went wrong reaching the assistant.', configured: false });
        this.asking = false;
        this.scrollToBottom();
        await this.voice.speak('Sorry, something went wrong reaching the assistant.');
        this.voiceActivity.continueConversation();
      }
    });

    setTimeout(() => this.scrollToBottom());
  }

  confirmAction(msg: ChatMessage): void {
    const action = msg.pendingAction;
    if (!action || this.triggering) return;

    this.triggering = true;
    this.chat.confirm(action).subscribe({
      next: (res) => {
        msg.actionResolved = 'confirmed';
        this.messages.push({ role: 'assistant', text: res.message });
        this.triggering = false;
        this.scrollToBottom();
      },
      error: () => {
        msg.actionResolved = 'confirmed';
        this.messages.push({ role: 'assistant', text: 'Something went wrong performing that action.' });
        this.triggering = false;
        this.scrollToBottom();
      }
    });
  }

  cancelAction(msg: ChatMessage): void {
    msg.actionResolved = 'cancelled';
    this.messages.push({ role: 'assistant', text: 'Okay, not doing that.' });
    this.scrollToBottom();
  }

  private scrollToBottom(): void {
    setTimeout(() => this.scrollAnchor?.nativeElement.scrollIntoView({ behavior: 'smooth' }));
  }
}
