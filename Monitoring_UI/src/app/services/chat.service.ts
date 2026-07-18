import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

// Generic envelope — Type selects which action /api/chat/confirm performs, Params carries
// whatever that action needs. The frontend never interprets Params itself; it's echoed back
// to /api/chat/confirm verbatim, and the server re-validates everything again there.
export interface PendingAction {
  type: string;
  params: Record<string, unknown>;
}

export interface ChatResponse {
  answer: string;
  claudeConfigured: boolean;
  pendingAction: PendingAction | null;
}

export interface ChatConfirmResponse {
  ok: boolean;
  message: string;
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  constructor(private http: HttpClient) {}

  ask(question: string, environment?: string): Observable<ChatResponse> {
    return this.http.post<ChatResponse>('/api/chat/ask', { question, environment });
  }

  confirm(action: PendingAction): Observable<ChatConfirmResponse> {
    return this.http.post<ChatConfirmResponse>('/api/chat/confirm', { type: action.type, params: action.params });
  }
}
