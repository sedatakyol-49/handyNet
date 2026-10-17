import { TestBed } from '@angular/core/testing';
import { SessionStore, StoredToken, TOKEN_KEY, usableToken } from './session.store';
import { browserRefreshLock } from './token-refresher';

const inOneHour = () => new Date(Date.now() + 3600_000).toISOString();
const anHourAgo = () => new Date(Date.now() - 3600_000).toISOString();

function stored(overrides: Partial<StoredToken> = {}): StoredToken {
  return { accessToken: 'a1', expiresAt: inOneHour(), refreshToken: 'r1', refreshExpiresAt: inOneHour(), userId: 'u1', ...overrides };
}

describe('usableToken', () => {
  it('keeps a session whose access token expired while the refresh token is still valid', () => {
    expect(usableToken(stored({ expiresAt: anHourAgo() }))).not.toBeNull();
  });

  it('drops a session when both tokens expired, or the access token expired without refresh token', () => {
    expect(usableToken(stored({ expiresAt: anHourAgo(), refreshExpiresAt: anHourAgo() }))).toBeNull();
    expect(usableToken(stored({ expiresAt: anHourAgo(), refreshToken: null }))).toBeNull();
    expect(usableToken(null)).toBeNull();
  });
});

describe('SessionStore cross-tab sync', () => {
  let session: SessionStore;

  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem(TOKEN_KEY, JSON.stringify(stored()));
    TestBed.configureTestingModule({});
    session = TestBed.inject(SessionStore);
  });

  function otherTabWrites(value: StoredToken | null): void {
    const newValue = value ? JSON.stringify(value) : null;
    if (newValue) localStorage.setItem(TOKEN_KEY, newValue);
    else localStorage.removeItem(TOKEN_KEY);
    window.dispatchEvent(new StorageEvent('storage', { key: TOKEN_KEY, newValue }));
  }

  it('adopts a pair rotated by another tab', () => {
    expect(session.refreshToken()).toBe('r1');
    otherTabWrites(stored({ accessToken: 'a2', refreshToken: 'r2' }));
    expect(session.token()).toBe('a2');
    expect(session.refreshToken()).toBe('r2');
    expect(session.signedOutElsewhere()).toBe(0);
  });

  it('signs out when another tab signed out', () => {
    session.setMe({ userId: 'u1', email: 'a@b.de', displayName: 'A', isPlatformAdmin: false, emailVerified: true, memberships: [] });
    otherTabWrites(null);
    expect(session.isAuthenticated()).toBe(false);
    expect(session.me()).toBeNull();
    expect(session.signedOutElsewhere()).toBe(1);
  });

  it('forgets /api/me when another tab signed in as a different user', () => {
    session.setMe({ userId: 'u1', email: 'a@b.de', displayName: 'A', isPlatformAdmin: false, emailVerified: true, memberships: [] });
    otherTabWrites(stored({ accessToken: 'b1', refreshToken: 'rb', userId: 'u2' }));
    expect(session.token()).toBe('b1');
    expect(session.me()).toBeNull();
  });
});

describe('browserRefreshLock (localStorage lease fallback)', () => {
  it('runs tasks of concurrent callers one after another', async () => {
    localStorage.clear();
    const fakeWindow = { localStorage, setTimeout: window.setTimeout.bind(window), navigator: {} } as unknown as Window & typeof globalThis;
    const lockA = browserRefreshLock(fakeWindow, true);
    const lockB = browserRefreshLock(fakeWindow, true);
    let running = 0;
    let maxRunning = 0;
    const task = async () => {
      running++;
      maxRunning = Math.max(maxRunning, running);
      await new Promise((r) => setTimeout(r, 30));
      running--;
      return 'done';
    };
    const results = await Promise.all([lockA.run(task), lockB.run(task), lockA.run(task)]);
    expect(results).toEqual(['done', 'done', 'done']);
    expect(maxRunning).toBe(1);
    expect(localStorage.getItem('hn.auth.refresh-lease')).toBeNull();
  });

  it('uses the Web Locks API when the browser has it', async () => {
    const request = vi.fn((_name: string, _opts: unknown, cb: () => Promise<string>) => cb());
    const fakeWindow = { navigator: { locks: { request } } } as unknown as Window & typeof globalThis;
    await expect(browserRefreshLock(fakeWindow, true).run(async () => 'ok')).resolves.toBe('ok');
    expect(request).toHaveBeenCalledWith('hn.auth.refresh', { mode: 'exclusive' }, expect.any(Function));
  });
});
