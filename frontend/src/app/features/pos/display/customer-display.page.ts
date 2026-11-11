import { ChangeDetectionStrategy, Component, DestroyRef, PLATFORM_ID, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { SessionStore } from '../../../core/auth/session.store';
import { CompanyBrandingStore } from '../../../core/company/company-branding.store';
import { I18nService, LANGUAGES, Lang } from '../../../core/i18n/i18n.service';
import { TranslatePipe } from '../../../core/i18n/translate.pipe';
import { StorageService } from '../../../core/storage.service';
import { CustomerDisplayViewComponent } from './customer-display.view';
import { DisplayState, displayChannelName, idleDisplayState, parseDisplayMessage } from './display-state';

/** The till's theme choice (`hn.pos.theme`), shared by the local display. */
export function displayDark(stored: string | null, systemDark: boolean): boolean {
  return stored === 'dark' ? true : stored === 'light' ? false : systemDark;
}

/** Language of a display state if the app has it (the display follows the till's language). */
export function displayLang(locale: string | null | undefined): Lang | null {
  const code = (locale ?? '').slice(0, 2).toLowerCase();
  return LANGUAGES.find((l) => l.code === code)?.code ?? null;
}

/**
 * Local customer display (`/kasse/:registerId/kundendisplay`): a second browser window on the customer-facing screen.
 * It listens on the register's BroadcastChannel — same browser profile as the till, no backend involved — and shows
 * the idle screen until the till publishes a state.
 */
@Component({
  selector: 'hn-customer-display-page',
  imports: [CustomerDisplayViewComponent, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-customer-display-view [state]="state()" [shopName]="shopName()" [logoUrl]="branding.logoUrl()" [dark]="dark()" [notice]="unsupported ? ('pos.display.unsupported' | t) : null">
      @if (canFullscreen && !fullscreen()) {
        <button cdAction type="button" class="pos-soft min-h-9 shrink-0 px-3 text-xs text-pos-muted" (click)="enterFullscreen()" data-testid="cd-fullscreen">{{ 'pos.display.fullscreen' | t }}</button>
      }
    </hn-customer-display-view>
  `,
})
export class CustomerDisplayPage {
  readonly registerId = input.required<string>();

  private readonly i18n = inject(I18nService);
  private readonly storage = inject(StorageService);
  private readonly session = inject(SessionStore);
  protected readonly branding = inject(CompanyBrandingStore);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  protected readonly state = signal<DisplayState>(idleDisplayState(this.i18n.lang()));
  protected readonly shopName = computed(() => this.branding.profile()?.name ?? this.session.membership()?.organizationName ?? null);
  protected readonly unsupported = this.browser && typeof BroadcastChannel === 'undefined';
  protected readonly canFullscreen = this.browser && typeof document.documentElement.requestFullscreen === 'function';
  protected readonly fullscreen = signal(false);

  private readonly theme = signal(this.storage.get('hn.pos.theme'));
  private readonly systemDark = signal(false);
  protected readonly dark = computed(() => displayDark(this.theme(), this.systemDark()));

  constructor() {
    if (!this.browser) return;
    const destroyRef = inject(DestroyRef);
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    if (media) {
      this.systemDark.set(media.matches);
      const onScheme = (e: MediaQueryListEvent) => this.systemDark.set(e.matches);
      media.addEventListener?.('change', onScheme);
      destroyRef.onDestroy(() => media.removeEventListener?.('change', onScheme));
    }
    // The till's theme choice lives in localStorage: follow it when the cashier changes it in the other window.
    const onStorage = (e: StorageEvent) => {
      if (e.key === 'hn.pos.theme') this.theme.set(e.newValue);
    };
    const onFullscreen = () => this.fullscreen.set(!!document.fullscreenElement);
    window.addEventListener('storage', onStorage);
    document.addEventListener('fullscreenchange', onFullscreen);
    destroyRef.onDestroy(() => {
      window.removeEventListener('storage', onStorage);
      document.removeEventListener('fullscreenchange', onFullscreen);
    });

    effect((onCleanup) => {
      const id = this.registerId();
      if (typeof BroadcastChannel === 'undefined') return;
      const channel = new BroadcastChannel(displayChannelName(id));
      channel.onmessage = (e: MessageEvent) => {
        const m = parseDisplayMessage(e.data);
        if (m?.type === 'state') this.state.set(m.state);
      };
      channel.postMessage({ type: 'hello' });
      onCleanup(() => channel.close());
    });
    // Follow the till's language.
    effect(() => {
      const lang = displayLang(this.state().locale);
      if (lang && lang !== untracked(() => this.i18n.lang())) void this.i18n.use(lang);
    });
  }

  protected enterFullscreen(): void {
    void document.documentElement.requestFullscreen?.().catch(() => undefined);
  }
}
