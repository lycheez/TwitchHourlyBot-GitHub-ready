import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ApiService } from './api.service';

describe('ApiService', () => {
  let api: ApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(ApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('posts a new channel', () => {
    api.addChannel('alpha').subscribe();
    const req = http.expectOne('/api/channels');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'alpha' });
    req.flush({});
  });

  it('adds a rule with timeout', () => {
    api.addRule(3, 'word', 120).subscribe();
    const req = http.expectOne('/api/channels/3/rules');
    expect(req.request.body).toEqual({ keyword: 'word', timeoutSeconds: 120 });
    req.flush({});
  });

  it('updates a rule through the channel route', () => {
    api.updateRule(3, { id: 7, keyword: 'w', enabled: false, timeoutSeconds: 30 }).subscribe();
    const req = http.expectOne('/api/channels/3/rules/7');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ enabled: false, timeoutSeconds: 30 });
    req.flush({});
  });

  it('checks a message', () => {
    api.check(3, 'hello').subscribe();
    const req = http.expectOne('/api/channels/3/check');
    expect(req.request.body).toEqual({ message: 'hello' });
    req.flush({ matched: false, keyword: null, timeoutSeconds: null });
  });
});
