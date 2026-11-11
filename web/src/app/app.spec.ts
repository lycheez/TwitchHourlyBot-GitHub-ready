import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { App } from './app';

const channels = [
  { id: 1, name: 'alpha', isEnabled: true, keywordProtectionEnabled: true, ruleCount: 1 },
  { id: 2, name: 'beta', isEnabled: true, keywordProtectionEnabled: true, ruleCount: 0 },
];
const rules = [{ id: 10, keyword: 'spoiler', enabled: true, timeoutSeconds: 120 }];

describe('App', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function load() {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    http.expectOne('/api/channels').flush(channels);
    http.expectOne('/api/channels/1/rules').flush(rules);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('lists channels and shows the first channel rules', async () => {
    const fixture = await load();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('#alpha');
    expect(text).toContain('#beta');
    expect(text).toContain('spoiler');
    expect(text).toContain('120s');
  });

  it('shows an empty state when there are no channels', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    http.expectOne('/api/channels').flush([]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('No channels yet.');
  });

  it('shows the API error message', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    http.expectOne('/api/channels').flush({ error: 'boom' }, { status: 409, statusText: 'Conflict' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[role=alert]')?.textContent).toContain('boom');
  });

  it('shows a friendly message when the API is unreachable', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    http.expectOne('/api/channels').error(new ProgressEvent('error'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Cannot reach the API');
  });
});
