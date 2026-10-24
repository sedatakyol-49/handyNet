import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { filter, firstValueFrom, map } from 'rxjs';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService, Lang } from '../../core/i18n/i18n.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { PosContext, PosTheme } from './pos-context';
import { canSeeSessionReport, posRights, posRightsKey } from './pos-rights';
import { PosFiscalStatus, PosTseBannerComponent, PosTsePillComponent } from './pos-fiscal-status';
import { PosCashierStore } from './pos-cashier.store';
import { PinEntry, PosPinDialogComponent } from './pos-pin-dialog';
import { posErrorText } from './pos-error';
import { PosApi } from '../../core/api/handynet-api';
import { POS_CONFIGURABLE_PERMISSIONS, PosCashierOption } from '../../core/api/models';
import { ToastService } from '../../core/ui-state/toast.service';
import { CompanyBrandingStore } from '../../core/company/company-branding.store';

/**
 * Standalone, full-screen POS app (route `/kasse`, outside the back-office shell): a minimal top bar with register,
 * session state, cashier, online state, clock and the menu (receipts, sessions, cash in/out and closing, theme,
 * language, back to the back office). Pages are rendered below it and own their scrolling.
 */
@Component({
  selector: 'hn-pos-app',
  imports: [RouterOutlet, RouterLink, NgTemplateOutlet, TranslatePipe, PosTsePillComponent, PosTseBannerComponent, PosPinDialogComponent],
  providers: [PosContext, PosFiscalStatus],
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
        <span class="grid size-[30px] place-items-center rounded-lg bg-pos-accent text-xs font-extrabold text-pos-accent-ink" aria-hidden="true">SH</span>
        @if (branding.logoUrl(); as logo) {
          <span class="flex h-9 items-center rounded-md bg-white px-1.5 ring-1 ring-pos-line" data-testid="pos-topbar-logo">
            <img [src]="logo" alt="" class="h-7 w-auto max-w-[6.5rem] object-contain" />
          </span>
        }
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
        @if (ctx.registerId()) {
          <hn-pos-tse-pill />
        }
        <span class="flex items-center gap-1.5 text-xs" [class]="ctx.online() ? 'text-pos-muted' : 'font-semibold text-pos-danger'" role="status" data-testid="pos-online">
          <span class="inline-block size-2 rounded-full" [class]="ctx.online() ? 'bg-pos-ok' : 'bg-pos-danger'" aria-hidden="true"></span>
          <span class="lg:not-sr-only" [class.sr-only]="ctx.online()">{{ (ctx.online() ? 'pos.app.online' : 'pos.app.offline') | t }}</span>
        </span>
        <time class="hidden text-sm font-semibold text-pos-muted tabular-nums sm:inline" [attr.datetime]="ctx.now().toISOString()" data-testid="pos-clock">{{ clock() }}</time>
        <span class="hidden max-w-44 items-center gap-2 text-[13px] text-pos-muted md:flex" data-testid="pos-cashier">
          <span class="grid size-7 shrink-0 place-items-center rounded-full bg-pos-key text-xs font-bold text-pos-ink" aria-hidden="true">{{ cashierInitial() }}</span>
          <span class="truncate"><span class="sr-only">{{ 'pos.receipt.cashier' | t }}: </span>{{ cashiers.actingName() }}</span>
        </span>
        @if (cashiers.approval(); as a) {
          <span class="flex min-h-9 items-center gap-1.5 rounded-full bg-pos-accent-soft px-2.5 text-xs font-semibold text-pos-accent-text" role="status"
            data-testid="pos-approval-active">
            {{ 'pos.approval.active' | t: { name: a.approverName ?? '' } }}
            <button type="button" class="grid size-6 place-items-center rounded-full hover:bg-pos-key" (click)="cashiers.clearApproval()"
              [attr.aria-label]="'pos.approval.discard' | t">✕</button>
          </span>
        }
        @if (ctx.registerId()) {
          <button type="button" class="pos-soft min-h-11 px-2.5 text-[13px]" (click)="openSwitch()" data-testid="pos-switch-cashier">
            <svg class="size-5" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" aria-hidden="true">
              <path d="M16 3l3 3-3 3M19 6H9a4 4 0 0 0-4 4M8 21l-3-3 3-3M5 18h10a4 4 0 0 0 4-4" />
            </svg>
            <span class="hidden lg:inline">{{ 'pos.cashier.switch' | t }}</span>
            <span class="sr-only lg:hidden">{{ 'pos.cashier.switch' | t }}</span>
          </button>
        }

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
                <p class="font-semibold text-slate-900">{{ cashiers.actingName() }}</p>
                @if (cashiers.cashier()) {
                  <p class="truncate text-slate-500" data-testid="pos-menu-device">{{ 'pos.cashier.device' | t: { name: session.me()?.displayName ?? '' } }}</p>
                }
                <p class="truncate text-slate-500">{{ session.membership()?.organizationName }}</p>
              </div>
              @if (ctx.registerId(); as rid) {
                <button type="button" role="menuitem" class="pos-menu-item" (click)="closeMenu(); lockTill()" data-testid="pos-menu-lock">{{ 'pos.cashier.lock' | t }}</button>
                @if (missingRights().length) {
                  <button type="button" role="menuitem" class="pos-menu-item" (click)="closeMenu(); ctx.requestApproval(missingRights())" data-testid="pos-menu-approval">
                    {{ 'pos.approval.menu' | t }}
                  </button>
                }
              }
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
    <hn-pos-tse-banner />

    <main id="pos-main" class="min-h-0 flex-1 overflow-auto" tabindex="-1" [attr.inert]="pinRequired() || null">
      <router-outlet />
    </main>

    @if (pinRequired()) {
      <div class="fixed inset-x-0 top-14 bottom-0 z-30 bg-pos-surface/95" data-testid="pos-locked" aria-hidden="true"></div>
    }
    <hn-pos-pin-dialog [open]="!!approvalWanted() && !pinRequired()" [title]="'pos.approval.title' | t" testId="pos-approval-dialog"
      [hint]="'pos.approval.hint' | t: { rights: approvalRightsText() }" [submitLabel]="'pos.approval.approve' | t" [emptyText]="'pos.approval.nobody' | t"
      [members]="approvers()" [busy]="pinBusy()" [error]="pinError()" (entered)="approve($event)" (closed)="ctx.approvalRequest.set(null)" />
    <hn-pos-pin-dialog [open]="pinOpen()" [title]="(pinRequired() ? 'pos.cashier.lockedTitle' : 'pos.cashier.switch') | t"
      [hint]="pinRequired() ? ('pos.cashier.lockedHint' | t) : null" [submitLabel]="'pos.cashier.signIn' | t" [emptyText]="'pos.cashier.nobody' | t"
      [members]="members()" [busy]="pinBusy()" [error]="pinError()" [extraAction]="pinRequired()" (entered)="signIn($event)" (closed)="closeSwitch()">
      <a routerLink="/kasse" class="text-sm font-semibold text-pos-accent-text underline" (click)="closeSwitch()" data-testid="pos-locked-leave">{{ 'pos.app.registerSelection' | t }}</a>
    </hn-pos-pin-dialog>
  `,
})
export class PosAppComponent {
  protected readonly ctx = inject(PosContext);
  /** Company logo next to the SEHA mark (on a white plate so it reads on the dark theme too). */
  protected readonly branding = inject(CompanyBrandingStore);
  protected readonly session = inject(SessionStore);
  protected readonly i18n = inject(I18nService);
  private readonly router = inject(Router);
  private readonly menuButton = viewChild<ElementRef<HTMLButtonElement>>('menuButton');

  protected readonly themes: PosTheme[] = ['system', 'light', 'dark'];
  protected readonly cashiers = inject(PosCashierStore);
  private readonly api = inject(PosApi);
  private readonly toast = inject(ToastService);
  protected readonly rights = computed(() => posRights(this.cashiers.membership()));
  protected readonly canReport = computed(() => canSeeSessionReport(this.rights()));
  protected readonly menuOpen = signal(false);

  // ---- cashier switching / lock (shared device)
  protected readonly switchOpen = signal(false);
  protected readonly members = signal<PosCashierOption[]>([]);
  protected readonly pinBusy = signal(false);
  protected readonly pinError = signal<string | null>(null);
  /** The till of this register is locked, or the signed-in cashier belongs to another register: PIN needed. */
  protected readonly pinRequired = computed(() => {
    const rid = this.ctx.registerId();
    return this.cashiers.isLocked(rid) || this.cashiers.boundElsewhere(rid);
  });
  protected readonly pinOpen = computed(() => this.switchOpen() || this.pinRequired());

  // ---- manager approval (owner/manager PIN for the cashier's next action)
  protected readonly approvalWanted = computed(() => {
    const wanted = this.ctx.approvalRequest();
    return wanted?.length && this.ctx.registerId() ? wanted : null;
  });
  /** POS rights the till currently lacks (offered in the menu for an approval). */
  protected readonly missingRights = computed(() => {
    const held = new Set(this.cashiers.membership()?.permissions ?? []);
    return POS_CONFIGURABLE_PERMISSIONS.filter((p) => !held.has(p));
  });
  protected readonly approvers = computed(() => {
    const self = this.cashiers.cashier()?.userId ?? this.session.me()?.userId;
    return this.members().filter((m) => (m.role === 'Owner' || m.role === 'Manager') && m.userId !== self);
  });
  protected readonly approvalRightsText = computed(() =>
    (this.approvalWanted() ?? []).map((p) => this.i18n.t('team.posRights.names.' + posRightsKey(p))).join(', '),
  );

  constructor() {
    // Load the members with a PIN whenever a pad is shown.
    effect(() => {
      if (this.pinOpen() || this.approvalWanted()) untracked(() => {
        this.pinError.set(null);
        void this.loadMembers();
      });
    });
    // Auto-lock after the register's idle minutes (the API enforces the same on the cashier token).
    effect((onCleanup) => {
      const cashier = this.cashiers.cashier();
      const minutes = cashier?.autoLockMinutes;
      if (!cashier || !minutes || typeof document === 'undefined') return;
      let timer: ReturnType<typeof setTimeout> | null = null;
      const arm = () => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => void this.lockTill(), minutes * 60_000);
      };
      const events = ['pointerdown', 'keydown'] as const;
      events.forEach((e) => document.addEventListener(e, arm, { passive: true }));
      arm();
      onCleanup(() => {
        if (timer) clearTimeout(timer);
        events.forEach((e) => document.removeEventListener(e, arm));
      });
    });
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
  protected readonly cashierInitial = computed(() => (this.cashiers.actingName() ?? '?').charAt(0).toUpperCase());

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

  protected openSwitch(): void {
    this.pinError.set(null);
    this.switchOpen.set(true);
  }

  protected closeSwitch(): void {
    this.switchOpen.set(false);
    this.pinError.set(null);
    // Closing the pad of a locked till leaves the till (back to the register selection).
    if (this.pinRequired()) void this.router.navigate(['/kasse']);
  }

  private async loadMembers(): Promise<void> {
    try {
      this.members.set(await firstValueFrom(this.api.cashiers()));
    } catch (e) {
      this.pinError.set(posErrorText(this.i18n, e));
    }
  }

  protected async signIn(entry: PinEntry): Promise<void> {
    const rid = this.ctx.registerId();
    if (!rid) return;
    this.pinBusy.set(true);
    this.pinError.set(null);
    try {
      const result = await firstValueFrom(this.api.signInCashier(rid, entry.userId, entry.pin));
      this.cashiers.signedIn(result);
      this.switchOpen.set(false);
      this.toast.success(this.i18n.t('pos.cashier.signedIn', { name: result.cashier.displayName ?? '' }));
    } catch (e) {
      this.pinError.set(posErrorText(this.i18n, e));
      void this.loadMembers(); // a lockout shows up in the list
    } finally {
      this.pinBusy.set(false);
    }
  }

  protected async approve(entry: PinEntry): Promise<void> {
    const rid = this.ctx.registerId();
    const permissions = this.approvalWanted();
    if (!rid || !permissions) return;
    this.pinBusy.set(true);
    this.pinError.set(null);
    try {
      const grant = await firstValueFrom(this.api.requestApproval(rid, { approverUserId: entry.userId, pin: entry.pin, permissions }));
      this.cashiers.approved(rid, grant);
      this.ctx.approvalRequest.set(null);
      this.toast.success(this.i18n.t('pos.approval.granted', { name: grant.approverName ?? '' }));
    } catch (e) {
      this.pinError.set(posErrorText(this.i18n, e));
      void this.loadMembers();
    } finally {
      this.pinBusy.set(false);
    }
  }

  /** Locks the till: ends the cashier sign-in on the server; the PIN pad is needed to continue. */
  protected async lockTill(): Promise<void> {
    const rid = this.ctx.registerId() ?? this.cashiers.cashier()?.registerId;
    if (!rid) return;
    try {
      if (this.cashiers.token()) await firstValueFrom(this.api.lockCashier());
    } catch {
      /* the token is discarded anyway */
    }
    this.cashiers.lock(rid);
    this.switchOpen.set(false);
  }

  protected setLanguage(lang: string): void {
    void this.i18n.use(lang as Lang);
  }
}
