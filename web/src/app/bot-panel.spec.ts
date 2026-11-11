import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BotPanel } from './bot-panel';

const settings = {
  intervalHours: 2, messageTemplate: 'hello {channel}',
  activityEnabled: true, activityIntervalMinutes: 30, redirectUri: 'http://localhost:4200/api/auth/callback',
};

describe('BotPanel', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [BotPanel],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function load(auth = { connected: false, login: null as string | null, userId: null as string | null, scopesOk: false, configured: true }, bot = {}) {
    const fixture = TestBed.createComponent(BotPanel);
    fixture.detectChanges();
    http.expectOne('/api/settings').flush(settings);
    http.expectOne('/api/auth/status').flush(auth);
    http.expectOne('/api/bot').flush({ running: false, connected: auth.connected, login: auth.login, nextEventAt: null, keywordRuleCount: 0, channels: [], ...bot });
    http.expectOne('/api/log').flush(['12:00:00  ▶ Bot started']);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  const text = (f: { nativeElement: unknown }) => (f.nativeElement as HTMLElement).textContent ?? '';

  it('offers a Log in with Twitch button when the server is configured', async () => {
    const fixture = await load();
    const login = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes('Log in with Twitch'))!;
    expect(login.disabled).toBe(false);
    expect(text(fixture)).not.toContain('Client Secret');
    expect((fixture.nativeElement as HTMLElement).querySelector('input[type=password]')).toBeNull();
  });

  it('explains the server setup when no Twitch application is configured', async () => {
    const fixture = await load({ connected: false, login: null, userId: null, scopesOk: false, configured: false });
    expect(text(fixture)).toContain('Twitch__ClientId');
    expect(text(fixture)).toContain('http://localhost:4200/api/auth/callback');
    const buttons = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'));
    expect(buttons.some((b) => b.textContent?.includes('Log in with Twitch'))).toBe(false);
  });

  it('shows the connected account and a stopped bot', async () => {
    const fixture = await load({ connected: true, login: 'botuser', userId: '99', scopesOk: true, configured: true });
    expect(text(fixture)).toContain('botuser');
    expect(text(fixture)).toContain('STOPPED');
    expect(text(fixture)).toContain('Bot started');
  });

  it('shows channel rows while the bot is running', async () => {
    const fixture = await load(
      { connected: true, login: 'botuser', userId: '99', scopesOk: true, configured: true },
      { running: true, keywordRuleCount: 3, channels: [{ channel: 'alpha', status: 'LIVE', isLive: true, liveTime: '01:00:00', nextMessage: '00:30:00', lastMessage: 'hi', lastActivity: '', lastChecked: '' }] },
    );
    expect(text(fixture)).toContain('RUNNING');
    expect(text(fixture)).toContain('#alpha');
    expect(text(fixture)).toContain('00:30:00');
    expect(text(fixture)).toContain('3 rule(s)');
  });

  it('shows the API message when the bot cannot start', async () => {
    const fixture = await load();
    const start = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((b) => b.textContent?.includes('Start bot'))!;
    start.click();
    http.expectOne('/api/bot/start').flush({ error: 'Not connected to Twitch.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(text(fixture)).toContain('Not connected to Twitch.');
  });
});
