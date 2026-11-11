import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { ApiService, AuthStatus, BotStatus, Settings, messageOf } from './api.service';

@Component({
  selector: 'app-bot-panel',
  templateUrl: './bot-panel.html',
  styleUrl: './bot-panel.css',
})
export class BotPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly settings = signal<Settings | null>(null);
  protected readonly auth = signal<AuthStatus>({ connected: false, login: null, userId: null, scopesOk: false, configured: true });
  protected readonly bot = signal<BotStatus | null>(null);
  protected readonly log = signal<string[]>([]);
  protected readonly error = signal('');
  protected readonly notice = signal('');

  ngOnInit(): void {
    this.readLoginResult();
    this.api.settings().subscribe({ next: (s) => this.settings.set(s), error: (e) => this.error.set(messageOf(e)) });
    this.refresh();
    const timer = setInterval(() => this.refresh(), 5000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  /** Twitch sends the browser back to /?login=ok or /?login=error&message=... */
  private readLoginResult(): void {
    const params = new URLSearchParams(window.location.search);
    const result = params.get('login');
    if (!result) return;
    if (result === 'ok') this.notice.set('Connected to Twitch.');
    else this.error.set(params.get('message') ?? 'Twitch login failed.');
    window.history.replaceState({}, '', window.location.pathname);
  }

  protected refresh(): void {
    this.api.authStatus().subscribe({ next: (a) => this.auth.set(a), error: () => undefined });
    this.api.bot().subscribe({ next: (b) => this.bot.set(b), error: (e) => this.error.set(messageOf(e)) });
    this.api.log().subscribe({ next: (l) => this.log.set(l), error: () => undefined });
  }

  protected login(): void {
    this.error.set('');
    this.api.loginUrl().subscribe({
      next: (r) => window.location.assign(r.url),
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected logout(): void {
    this.api.logout().subscribe({
      next: () => this.refresh(),
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected start(): void {
    this.error.set('');
    this.api.startBot().subscribe({
      next: (b) => {
        this.bot.set(b);
        this.refresh();
      },
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected stop(): void {
    this.api.stopBot().subscribe({
      next: (b) => {
        this.bot.set(b);
        this.refresh();
      },
      error: (e) => this.error.set(messageOf(e)),
    });
  }

  protected saveBotSettings(
    hours: HTMLSelectElement,
    template: HTMLTextAreaElement,
    events: HTMLInputElement,
    eventMinutes: HTMLInputElement,
  ): void {
    this.error.set('');
    this.notice.set('');
    this.api
      .saveSettings({
        intervalHours: Number(hours.value),
        messageTemplate: template.value,
        activityEnabled: events.checked,
        activityIntervalMinutes: Number(eventMinutes.value),
      })
      .subscribe({
        next: (s) => {
          this.settings.set(s);
          this.notice.set('Settings saved.');
        },
        error: (e) => this.error.set(messageOf(e)),
      });
  }

  protected copy(text: string): void {
    void navigator.clipboard?.writeText(text);
    this.notice.set('Copied.');
  }
}
