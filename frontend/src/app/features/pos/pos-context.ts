import { DestroyRef, Injectable, PLATFORM_ID, TemplateRef, computed, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { CashSession } from '../../core/api/models';
import { StorageService } from '../../core/storage.service';
import { ReachabilityService } from './offline/reachability.service';

export type PosTheme = 'system' | 'light' | 'dark';
const THEME_KEY = 'hn.pos.theme';

/**
 * State shared by the POS app's top bar and its pages (provided by PosAppComponent): the register and cash session
 * the current page works with, the colour theme and the browser's online state.
 */
@Injectable()
export class PosContext {
  private readonly storage = inject(StorageService);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  /** Register of the current page (null on the register selection). */
  readonly registerId = signal<string | null>(null);
  readonly registerName = signal<string | null>(null);
  /** Open session of that register: undefined = not known yet, null = none. */
  readonly session = signal<CashSession | null | undefined>(undefined);
  /** Content the selling screen shows in the top bar (its parked tickets); null on other pages. */
  readonly topbarTickets = signal<TemplateRef<unknown> | null>(null);

  readonly theme = signal<PosTheme>(readTheme(this.storage.get(THEME_KEY)));
  private readonly systemDark = signal(false);
  readonly dark = computed(() => (this.theme() === 'system' ? this.systemDark() : this.theme() === 'dark'));

  private readonly reach = inject(ReachabilityService);
  /** Whether the till reaches the API (browser online state, failed requests and a periodic probe). */
  readonly online = this.reach.online;
  /** Minute-precision clock for the top bar. */
  readonly now = signal(new Date());

  constructor() {
    if (!this.browser) return;
    const destroyRef = inject(DestroyRef);
    const media = typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-color-scheme: dark)') : null;
    if (media) {
      this.systemDark.set(media.matches);
      const onScheme = (e: MediaQueryListEvent) => this.systemDark.set(e.matches);
      media.addEventListener?.('change', onScheme);
      destroyRef.onDestroy(() => media.removeEventListener?.('change', onScheme));
    }
    const stopWatching = this.reach.watch();
    const timer = setInterval(() => this.now.set(new Date()), 15_000);
    destroyRef.onDestroy(() => {
      stopWatching();
      clearInterval(timer);
    });
  }

  /** Rights a page asks a manager to approve for the next action (the app shell shows the PIN pad); null = none. */
  readonly approvalRequest = signal<string[] | null>(null);

  requestApproval(permissions: string[]): void {
    this.approvalRequest.set([...new Set(permissions)]);
  }

  setRegister(id: string | null, name: string | null): void {
    this.registerId.set(id);
    this.registerName.set(name);
  }

  setTheme(theme: PosTheme): void {
    this.theme.set(theme);
    this.storage.set(THEME_KEY, theme === 'system' ? null : theme);
  }
}

function readTheme(value: string | null): PosTheme {
  return value === 'light' || value === 'dark' ? value : 'system';
}
