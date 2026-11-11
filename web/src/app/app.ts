import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ApiService, Channel, CheckResult, Rule } from './api.service';

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

@Component({
  selector: 'app-root',
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly api = inject(ApiService);

  protected readonly channels = signal<Channel[]>([]);
  protected readonly selectedId = signal<number | null>(null);
  protected readonly rules = signal<Rule[]>([]);
  protected readonly error = signal('');
  protected readonly checkResult = signal<CheckResult | null>(null);
  protected readonly selected = computed(() => this.channels().find((c) => c.id === this.selectedId()) ?? null);
  protected readonly timeoutOptions = [30, 60, 120, 300, 600];

  ngOnInit(): void {
    this.loadChannels();
  }

  protected loadChannels(selectId?: number | null): void {
    this.api.channels().subscribe({
      next: (list) => {
        this.channels.set(list);
        const keep = selectId === undefined ? this.selectedId() : selectId;
        const next = list.find((c) => c.id === keep) ?? list[0] ?? null;
        if (next?.id !== this.selectedId() || selectId !== undefined) this.select(next);
      },
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected select(channel: Channel | null): void {
    this.selectedId.set(channel?.id ?? null);
    this.checkResult.set(null);
    this.rules.set([]);
    if (channel) this.loadRules(channel.id);
  }

  private loadRules(channelId: number): void {
    this.api.rules(channelId).subscribe({
      next: (r) => this.rules.set(r),
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected addChannel(input: HTMLInputElement): void {
    this.error.set('');
    this.api.addChannel(input.value).subscribe({
      next: (c) => {
        input.value = '';
        this.loadChannels(c.id);
      },
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected toggleChannel(channel: Channel, field: 'isEnabled' | 'keywordProtectionEnabled'): void {
    const enabled = field === 'isEnabled' ? !channel.isEnabled : channel.isEnabled;
    const protection = field === 'keywordProtectionEnabled' ? !channel.keywordProtectionEnabled : channel.keywordProtectionEnabled;
    this.api.updateChannel(channel.id, enabled, protection).subscribe({
      next: (updated) => this.channels.update((list) => list.map((c) => (c.id === updated.id ? updated : c))),
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected deleteChannel(channel: Channel): void {
    this.api.deleteChannel(channel.id).subscribe({
      next: () => this.loadChannels(this.selectedId() === channel.id ? null : undefined),
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected addRule(keyword: HTMLInputElement, timeout: HTMLSelectElement): void {
    const channel = this.selected();
    if (!channel) return;
    this.error.set('');
    this.api.addRule(channel.id, keyword.value, Number(timeout.value)).subscribe({
      next: () => {
        keyword.value = '';
        this.loadRules(channel.id);
        this.refreshCounts();
      },
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected toggleRule(rule: Rule): void {
    const channel = this.selected();
    if (!channel) return;
    this.api.updateRule(channel.id, { ...rule, enabled: !rule.enabled }).subscribe({
      next: (updated) => this.rules.update((list) => list.map((r) => (r.id === updated.id ? updated : r))),
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected deleteRule(rule: Rule): void {
    const channel = this.selected();
    if (!channel) return;
    this.api.deleteRule(channel.id, rule.id).subscribe({
      next: () => {
        this.rules.update((list) => list.filter((r) => r.id !== rule.id));
        this.refreshCounts();
      },
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected check(message: HTMLInputElement): void {
    const channel = this.selected();
    if (!channel) return;
    this.api.check(channel.id, message.value).subscribe({
      next: (r) => this.checkResult.set(r),
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  private refreshCounts(): void {
    this.api.channels().subscribe({ next: (list) => this.channels.set(list) });
  }
}
