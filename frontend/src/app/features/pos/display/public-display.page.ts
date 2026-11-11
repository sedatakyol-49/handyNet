import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, PLATFORM_ID, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { Subject, firstValueFrom, takeUntil } from 'rxjs';
import { I18nService } from '../../../core/i18n/i18n.service';
import { TranslatePipe } from '../../../core/i18n/translate.pipe';
import { displayDark, displayLang } from './customer-display.page';
import { CustomerDisplayViewComponent } from './customer-display.view';
import { DisplayApi, PublicDisplay } from './display-api';
import { DisplayState, idleDisplayState, parseDisplayState } from './display-state';

/** Wait before the next poll after a failed one: 2 s, 4 s, 8 s, then every 15 s. */
export function pollBackoffMs(failures: number): number {
  return Math.min(2000 * 2 ** Math.max(failures - 1, 0), 15_000);
}

/** What a failed poll means: the link is gone for good, or try again. */
export function pollErrorOutcome(status: number): 'gone' | 'retry' {
  // Unknown, expired and revoked links are the same 404 (410 is treated alike). Everything else is temporary.
  return status === 404 || status === 410 ? 'gone' : 'retry';
}

/**
 * Remote customer display (`/kundendisplay/:token`, no login): a tablet or another device at the counter. It long-polls
 * the public API for the state the till pushed; the token in the link is the only credential. The API keeps the state in
 * memory, so after an API restart the display is idle until the till's next push.
 */
@Component({
  selector: 'hn-public-display-page',
  imports: [CustomerDisplayViewComponent, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (gone()) {
      <main class="hn-pos fixed inset-0 grid place-items-center bg-pos-surface p-6 text-center text-pos-ink" [class.hn-pos-dark]="dark()" role="alert" data-testid="cd-gone">
        <div class="max-w-xl">
          <h1 class="text-3xl font-extrabold">{{ 'pos.display.goneTitle' | t }}</h1>
          <p class="mt-3 text-lg text-pos-muted">{{ 'pos.display.goneText' | t }}</p>
        </div>
      </main>
    } @else {
      <hn-customer-display-view [state]="state()" [shopName]="shopName()" [logoUrl]="logoUrl()" [dark]="dark()" [notice]="reconnecting() ? ('pos.display.reconnecting' | t) : null" />
      @if (canFullscreen && !fullscreen()) {
        <button type="button" class="hn-pos fixed end-3 bottom-3 z-10 min-h-11 rounded-full border border-pos-line bg-pos-panel px-4 text-sm font-semibold text-pos-muted opacity-80 shadow hover:opacity-100"
          [class.hn-pos-dark]="dark()" (click)="enterFullscreen()" data-testid="cd-fullscreen">{{ 'pos.display.fullscreen' | t }}</button>
      }
    }
  `,
})
export class PublicDisplayPage {
  readonly token = input.required<string>();

  private readonly api = inject(DisplayApi);
  private readonly i18n = inject(I18nService);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  private readonly stop$ = new Subject<void>();
  private stopped = false;
  private pollRun = 0;

  protected readonly state = signal<DisplayState>(idleDisplayState(this.i18n.lang()));
  protected readonly shopName = signal<string | null>(null);
  protected readonly logoUrl = signal<string | null>(null);
  protected readonly gone = signal(false);
  protected readonly reconnecting = signal(false);
  protected readonly canFullscreen = this.browser && typeof document.documentElement.requestFullscreen === 'function';
  protected readonly fullscreen = signal(false);

  private readonly systemDark = signal(false);
  /** `?theme=dark|light` on the link fixes the theme; otherwise the device's setting decides. */
  private readonly themeParam = inject(ActivatedRoute).snapshot.queryParamMap.get('theme');
  protected readonly dark = computed(() => displayDark(this.themeParam, this.systemDark()));

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
    const onFullscreen = () => this.fullscreen.set(!!document.fullscreenElement);
    document.addEventListener('fullscreenchange', onFullscreen);
    destroyRef.onDestroy(() => {
      document.removeEventListener('fullscreenchange', onFullscreen);
      this.stopped = true;
      this.stop$.next();
    });

    effect(() => {
      const token = this.token();
      untracked(() => void this.poll(token));
    });
    effect(() => {
      const lang = displayLang(this.state().locale);
      if (lang && lang !== untracked(() => this.i18n.lang())) void this.i18n.use(lang);
    });
  }

  private async poll(token: string): Promise<void> {
    const run = ++this.pollRun;
    let version: number | null = null;
    let failures = 0;
    while (!this.stopped && run === this.pollRun) {
      try {
        const res: PublicDisplay | null | undefined = await firstValueFrom(this.api.publicState(token, version).pipe(takeUntil(this.stop$)), { defaultValue: undefined });
        if (res === undefined) return; // destroyed while waiting
        failures = 0;
        this.reconnecting.set(false);
        if (res === null) continue; // 204: nothing changed during the long-poll
        version = res.version;
        this.shopName.set(res.shop?.name ?? null);
        this.logoUrl.set(this.api.assetUrl(res.shop?.logoUrl));
        this.state.set(parseDisplayState(res.state) ?? idleDisplayState(this.state().locale));
      } catch (e) {
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        if (pollErrorOutcome(status) === 'gone') {
          this.gone.set(true);
          return;
        }
        failures++;
        if (failures >= 2) this.reconnecting.set(true);
        await new Promise((resolve) => setTimeout(resolve, pollBackoffMs(failures)));
      }
    }
  }

  protected enterFullscreen(): void {
    void document.documentElement.requestFullscreen?.().catch(() => undefined);
  }
}
