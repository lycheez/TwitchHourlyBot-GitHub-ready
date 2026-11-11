import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface Channel {
  id: number;
  name: string;
  isEnabled: boolean;
  keywordProtectionEnabled: boolean;
  ruleCount: number;
}

export interface Rule {
  id: number;
  keyword: string;
  enabled: boolean;
  timeoutSeconds: number;
}

export interface CheckResult {
  matched: boolean;
  keyword: string | null;
  timeoutSeconds: number | null;
}

export function messageOf(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) return 'Cannot reach the API. Is it running?';
    const body = err.error;
    if (body?.error) return String(body.error);
    if (body?.errors) return Object.values<string[]>(body.errors).flat().join(' ');
    return `Request failed (${err.status}).`;
  }
  return 'Unexpected error.';
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api';

  channels(): Observable<Channel[]> {
    return this.http.get<Channel[]>(`${this.base}/channels`);
  }

  addChannel(name: string): Observable<Channel> {
    return this.http.post<Channel>(`${this.base}/channels`, { name });
  }

  updateChannel(id: number, isEnabled: boolean, keywordProtectionEnabled: boolean): Observable<Channel> {
    return this.http.put<Channel>(`${this.base}/channels/${id}`, { isEnabled, keywordProtectionEnabled });
  }

  deleteChannel(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/channels/${id}`);
  }

  rules(channelId: number): Observable<Rule[]> {
    return this.http.get<Rule[]>(`${this.base}/channels/${channelId}/rules`);
  }

  addRule(channelId: number, keyword: string, timeoutSeconds: number): Observable<Rule> {
    return this.http.post<Rule>(`${this.base}/channels/${channelId}/rules`, { keyword, timeoutSeconds });
  }

  updateRule(channelId: number, rule: Rule): Observable<Rule> {
    return this.http.put<Rule>(`${this.base}/channels/${channelId}/rules/${rule.id}`, {
      enabled: rule.enabled,
      timeoutSeconds: rule.timeoutSeconds,
    });
  }

  deleteRule(channelId: number, ruleId: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/channels/${channelId}/rules/${ruleId}`);
  }

  settings(): Observable<Settings> {
    return this.http.get<Settings>(`${this.base}/settings`);
  }

  saveSettings(update: SettingsUpdate): Observable<Settings> {
    return this.http.put<Settings>(`${this.base}/settings`, update);
  }

  authStatus(): Observable<AuthStatus> {
    return this.http.get<AuthStatus>(`${this.base}/auth/status`);
  }

  loginUrl(): Observable<{ url: string }> {
    return this.http.get<{ url: string }>(`${this.base}/auth/login-url`);
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${this.base}/auth/logout`, {});
  }

  bot(): Observable<BotStatus> {
    return this.http.get<BotStatus>(`${this.base}/bot`);
  }

  startBot(): Observable<BotStatus> {
    return this.http.post<BotStatus>(`${this.base}/bot/start`, {});
  }

  stopBot(): Observable<BotStatus> {
    return this.http.post<BotStatus>(`${this.base}/bot/stop`, {});
  }

  log(): Observable<string[]> {
    return this.http.get<string[]>(`${this.base}/log`);
  }

  check(channelId: number, message: string): Observable<CheckResult> {
    return this.http.post<CheckResult>(`${this.base}/channels/${channelId}/check`, { message });
  }
}

export interface Settings {
  clientId: string;
  hasClientSecret: boolean;
  intervalHours: number;
  messageTemplate: string;
  activityEnabled: boolean;
  activityIntervalMinutes: number;
  redirectUri: string;
}

export interface SettingsUpdate {
  clientId?: string;
  clientSecret?: string;
  intervalHours?: number;
  messageTemplate?: string;
  activityEnabled?: boolean;
  activityIntervalMinutes?: number;
}

export interface AuthStatus {
  connected: boolean;
  login: string | null;
  userId: string | null;
  scopesOk: boolean;
}

export interface ChannelStatus {
  channel: string;
  status: string;
  isLive: boolean;
  liveTime: string;
  nextMessage: string;
  lastMessage: string;
  lastActivity: string;
  lastChecked: string;
}

export interface BotStatus {
  running: boolean;
  connected: boolean;
  login: string | null;
  nextEventAt: string | null;
  keywordRuleCount: number;
  channels: ChannelStatus[];
}
