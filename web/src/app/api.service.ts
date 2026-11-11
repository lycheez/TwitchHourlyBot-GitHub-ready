import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
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

  check(channelId: number, message: string): Observable<CheckResult> {
    return this.http.post<CheckResult>(`${this.base}/channels/${channelId}/check`, { message });
  }
}
