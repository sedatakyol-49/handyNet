import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService, Lang } from '../../core/i18n/i18n.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { PosContext, PosTheme } from './pos-context';
import { canSeeSessionReport, posRights } from './pos-rights';

/**
 * Standalone, full-screen POS app (route `/kasse`, outside the back-office shell): a minimal top bar with register,
 * session state, cashier, online state, clock and the menu (receipts, sessions, cash in/out and closing, theme,
 * language, back to the back office). Pages are rendered below it and own their scrolling.
 */
@Component({
  selector: 'hn-pos-app',
  imports: [RouterOutlet, RouterLink, NgTemplateOutlet, TranslatePipe],
  providers: [PosContext],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'hn-pos fixed inset-0 flex flex-col bg-pos-surface text-pos-ink',
    '[class.hn-pos-dark]': 'ctx.dark()',
    '[attr.data-theme]': 'ctx.dark() ? "dark" : "light"',
    '(document:click)': 'onDocumentClick($event)',
  },
  template: `
    <a href="#pos-main" class="sr-only focus:not-sr-only focus:fixed focus:start-2 focus:top-2 focus:z-50 focus:rounded focus:bg-white focus:px-3 focus:py-2 focus:shadow">
      {{ 'shell.skipToContent' | t }}
    </a>
    <header class="hn-no-print flex min-h-14 shrink-0 flex-wrap items-center gap-x-2 border-b border-pos-line bg-pos-panel px-2 sm:gap-x-3.5 sm:px-3.5" data-testid="pos-topbar">
      <a routerLink="/kasse" class="flex min-h-11 shrink-0 items-center gap-2 rounded-lg px-0.5 font-semibold" [attr.aria-label]="'pos.app.registerSelection' | t">
        <span class="grid size-[30px] place-items-center rounded-lg bg-pos-accent text-xs font-extrabold text-pos-accent-ink" aria-hidden="true">HN</span>
        @if (!ctx.registerId()) {
          <span class="text-sm">{{ 'pos.app.title' | t }}</span>
        }
      </a>

      @if (ctx.registerId()) {
        <div class="min-w-0 shrink leading-tight">
          <p class="truncate text-sm font-bold" data-testid="pos-topbar-register">{{ ctx.registerName() ?? '…' }}</p>
          <p class="flex items-center gap-1.5 truncate text-xs text-pos-muted" data-testid="pos-topbar-session">
            <span class="inline-block size-[7px] shrink-0 rounded-full" [class]="sessionDot()" aria-hidden="true"></span>
            {{ sessionText() }}
          </p>
        </div>
        @if (!onTill()) {
          <a [routerLink]="['/kasse', ctx.registerId()]" class="pos-soft pos-soft-accent ms-1" data-testid="pos-topbar-sell">
            {{ 'pos.app.sell' | t }}
          </a>
        }
      }

      @if (ctx.topbarTickets(); as tickets) {
        <div class="order-last -mx-2 flex min-w-0 basis-full border-t border-pos-line px-2 sm:-mx-3.5 sm:px-3.5 md:order-none md:mx-0 md:flex-1 md:basis-auto md:border-0 md:px-0 md:ps-2">
          <ng-container [ngTemplateOutlet]="tickets" />
        </div>
      }

      <div class="ms-auto flex items-center gap-1 sm:gap-3.5">
        <span class="flex items-center gap-1.5 text-xs" [class]="ctx.online() ? 'text-pos-muted' : 'font-semibold text-pos-danger'" role="status" data-testid="pos-online">
          <span class="inline-block size-2 rounded-full" [class]="ctx.online() ? 'bg-pos-ok' : 'bg-pos-danger'" aria-hidden="true"></span>
          <span class="lg:not-sr-only" [class.sr-only]="ctx.online()">{{ (ctx.online() ? 'pos.app.online' : 'pos.app.offline') | t }}</span>
        </span>
        <time class="hidden text-sm font-semibold text-pos-muted tabular-nums sm:inline" [attr.datetime]="ctx.now().toISOString()" data-testid="pos-clock">{{ clock() }}</time>
        <span class="hidden max-w-44 items-center gap-2 text-[13px] text-pos-muted md:flex" data-testid="pos-cashier">
          <span class="grid size-7 shrink-0 place-items-center rounded-full bg-pos-key text-xs font-bold text-pos-ink" aria-hidden="true">{{ cashierInitial() }}</span>
          <span class="truncate"><span class="sr-only">{{ 'pos.receipt.cashier' | t }}: </span>{{ session.me()?.displayName }}</span>
        </span>

        <div class="relative" (keydown.escape)="onMenuEscape($event)">
          <button #menuButton type="button" class="grid size-11 place-items-center rounded-[10px] text-pos-ink hover:bg-pos-key"
            aria-haspopup="menu" [attr.aria-expanded]="menuOpen()" aria-controls="pos-menu" (click)="toggleMenu($event)"
            [attr.aria-label]="'pos.app.menu' | t" data-testid="pos-menu">
            <svg class="size-6" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M4 6h16v2H4zM4 11h16v2H4zM4 16h16v2H4z" /></svg>
          </button>
          @if (menuOpen()) {
            <div id="pos-menu" class="absolute end-0 top-full z-40 mt-1 w-72 rounded-xl border border-pos-line bg-pos-panel py-1 text-sm shadow-lg" role="menu"
              [attr.aria-label]="'pos.app.menu' | t" (click)="$event.stopPropagation()">
              <div class="border-b border-slate-100 px-3 py-2 text-xs">
                <p class="font-semibold text-slate-900">{{ session.me()?.displayName }}</p>
                <p class="truncate text-slate-500">{{ session.membership()?.organizationName }}</p>
              </div>
              @if (ctx.registerId(); as rid) {
                <a role="menuitem" class="pos-menu-item" [routerLink]="['/kasse', rid]" (click)="closeMenu()" data-testid="pos-menu-sell">{{ 'pos.app.sell' | t }}</a>
                <a role="menuitem" class="pos-menu-item" [routerLink]="['/kasse', rid, 'belege']" (click)="closeMenu()" data-testid="pos-menu-receipts">{{ 'pos.app.receipts' | t }}</a>
                <a role="menuitem" class="pos-menu-item" [routerLink]="['/kasse', rid, 'sitzungen']" (click)="closeMenu()" data-testid="pos-menu-sessions">{{ 'pos.app.sessions' | t }}</a>
                @if (ctx.session(); as s) {
                  @if (rights().cashMovement || rights().closeSession) {
                    <a role="menuitem" class="pos-menu-item" [routerLink]="['/kasse/sitzungen', s.id]" (click)="closeMenu()" data-testid="pos-menu-cash">{{ 'pos.app.cashAndClosing' | t }}</a>
                  }
                  @if (canReport()) {
                    <a role="menuitem" class="pos-menu-item" routerLink="/kasse/berichte" [queryParams]="{ sitzung: s.id }" (click)="closeMenu()" data-testid="pos-menu-report">{{ 'pos.app.sessionReport' | t }}</a>
                  }
                }
                @if (rights().manage) {
                  <a role="menuitem" class="pos-menu-item" [routerLink]="['/kasse', rid, 'preise']" (click)="closeMenu()" data-testid="pos-menu-prices">{{ 'pos.app.prices' | t }}</a>
                }
              }
              @if (rights().manage) {
                <a role="menuitem" class="pos-menu-item" routerLink="/kasse/berichte" (click)="closeMenu()" data-testid="pos-menu-reports">{{ 'pos.app.reports' | t }}</a>
              }
              <a role="menuitem" class="pos-menu-item" routerLink="/kasse" (click)="closeMenu()" data-testid="pos-menu-registers">{{ 'pos.app.registerSelection' | t }}</a>

              <div class="mt-1 border-t border-slate-100 px-3 pt-2 pb-1" role="group" [attr.aria-label]="'pos.app.theme' | t">
                <p class="mb-1 text-xs font-medium text-slate-500" aria-hidden="true">{{ 'pos.app.theme' | t }}</p>
                <div class="grid grid-cols-3 gap-1">
                  @for (t of themes; track t) {
                    <button type="button" role="menuitemradio" [attr.aria-checked]="ctx.theme() === t" (click)="ctx.setTheme(t)"
                      class="min-h-11 rounded-[10px] border text-xs font-semibold"
                      [class]="ctx.theme() === t ? 'border-transparent bg-pos-accent-soft text-pos-accent-text shadow-[inset_0_0_0_1.5px_var(--pos-accent)]' : 'border-pos-line text-pos-ink hover:bg-pos-key'"
                      [attr.data-testid]="'pos-theme-' + t">{{ 'pos.app.themes.' + t | t }}</button>
                  }
                </div>
              </div>
              <div class="px-3 pt-1 pb-2">
                <label for="pos-lang" class="mb-1 block text-xs font-medium text-slate-500">{{ 'shell.language' | t }}</label>
                <select id="pos-lang" class="hn-control min-h-11" [value]="i18n.lang()" (change)="setLanguage($any($event.target).value)">
                  @for (l of i18n.languages; track l.code) {
                    <option [value]="l.code" [selected]="l.code === i18n.lang()">{{ l.label }}</option>
                  }
                </select>
              </div>
              <a role="menuitem" class="pos-menu-item border-t border-slate-100" routerLink="/app/uebersicht" (click)="closeMenu()" data-testid="pos-menu-backoffice">
                <span class="inline-block rtl:-scale-x-100" aria-hidden="true">←</span> {{ 'pos.app.backOffice' | t }}
              </a>
            </div>
          }
        </div>
      </div>
    </header>

    <main id="pos-main" class="min-h-0 flex-1 overflow-auto" tabindex="-1">
      <router-outlet />
    </main>
  `,
})
export class PosAppComponent {
  protected readonly ctx = inject(PosContext);
  protected readonly session = inject(SessionStore);
  protected readonly i18n = inject(I18nService);
  private readonly router = inject(Router);
  private readonly menuButton = viewChild<ElementRef<HTMLButtonElement>>('menuButton');

  protected readonly themes: PosTheme[] = ['system', 'light', 'dark'];
  protected readonly rights = computed(() => posRights(this.session.membership()));
  protected readonly canReport = computed(() => canSeeSessionReport(this.rights()));
  protected readonly menuOpen = signal(false);

  constructor() {
    // The acting organization changed (switched or another user signed in in another tab): the open register belongs
    // to the previous organization, so go back to register selection.
    let previousOrg: string | null = null;
    effect(() => {
      const org = this.session.organizationId();
      if (org && previousOrg && org !== previousOrg) untracked(() => void this.router.navigate(['/kasse']));
      if (org) previousOrg = org;
    });
  }

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map((e) => (e as NavigationEnd).urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );
  /** The selling screen of the current register (`/kasse/:registerId`). */
  protected readonly onTill = computed(() => {
    const id = this.ctx.registerId();
    return !!id && this.url().split(/[?#]/)[0] === `/kasse/${id}`;
  });

  protected readonly clock = computed(() =>
    new Intl.DateTimeFormat(this.i18n.locale(), { hour: '2-digit', minute: '2-digit' }).format(this.ctx.now()),
  );
  protected readonly cashierInitial = computed(() => (this.session.me()?.displayName ?? '?').charAt(0).toUpperCase());

  protected readonly sessionText = computed(() => {
    const s = this.ctx.session();
    if (s === undefined) return this.i18n.t('common.loading');
    if (!s) return this.i18n.t('pos.app.noSession');
    const since = new Intl.DateTimeFormat(this.i18n.locale(), { hour: '2-digit', minute: '2-digit', day: '2-digit', month: '2-digit' }).format(new Date(s.openedAt));
    return this.i18n.t('pos.till.sessionOpen', { since });
  });
  protected readonly sessionDot = computed(() => {
    const s = this.ctx.session();
    return s ? 'bg-pos-ok' : s === null ? 'bg-slate-400' : 'bg-slate-300';
  });

  protected toggleMenu(e: Event): void {
    e.stopPropagation();
    this.menuOpen.update((v) => !v);
  }

  protected closeMenu(refocus = false): void {
    if (!this.menuOpen()) return;
    this.menuOpen.set(false);
    if (refocus) this.menuButton()?.nativeElement.focus();
  }

  /** Esc closes an open menu and must not reach the selling screen (where Esc cancels step by step). */
  protected onMenuEscape(e: Event): void {
    if (!this.menuOpen()) return;
    e.stopPropagation();
    e.preventDefault();
    this.closeMenu(true);
  }

  protected onDocumentClick(e: MouseEvent): void {
    if (this.menuOpen() && !(e.target as HTMLElement | null)?.closest?.('#pos-menu')) this.menuOpen.set(false);
  }

  protected setLanguage(lang: string): void {
    void this.i18n.use(lang as Lang);
  }
}
