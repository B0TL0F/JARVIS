import { Component, ElementRef, OnDestroy, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { ChatService, PendingAction } from '../services/chat.service';
import { StatusService } from '../services/status.service';

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

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.css'
})
export class ChatComponent implements OnInit, OnDestroy {
  messages: ChatMessage[] = [
    { role: 'assistant', text: 'Ask me about current module status, recent incidents, pipeline health — or, if you\'re an admin, tell me to trigger/cancel/delete/rename a pipeline, manage targets or users, or update alert settings. I\'ll always confirm before making any change.' }
  ];
  question = '';
  asking = false;
  triggering = false;
  selectedEnvironment: string | null = null;

  voiceSupported = false;
  listening = false;

  @ViewChild('scrollAnchor') scrollAnchor?: ElementRef<HTMLElement>;

  private sub?: Subscription;
  private recognition: SpeechRecognitionLike | null = null;

  constructor(
    private chat: ChatService,
    private statusService: StatusService
  ) {}

  ngOnInit(): void {
    this.sub = this.statusService.environment$.subscribe((env) => (this.selectedEnvironment = env));
    this.setupSpeechRecognition();
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
    this.recognition?.stop();
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
