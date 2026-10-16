import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api/api-problem';
import { PosApi } from '../../core/api/handynet-api';
import { CashSession, Receipt } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';
import { PosSubnavComponent } from './pos-subnav';
import {
  CartLine,
  LastPrices,
  PendingAttempt,
  TILL_SHORTCUTS,
  addToCart,
  attemptFor,
  buildSaleRequest,
  cardRest,
  cartTotals,
  cashSuggestions,
  changeQuantity,
  exactCash,
  keepAttemptAfter,
  lineAmounts,
  maxQuantity,
  minorToPlain,
  newIdempotencyKey,
  parseAmount,
  problemDetail,
  problemStatus,
  settle,
  tillShortcut,
} from './pos.logic';
import { ReceiptViewComponent } from './receipt-view';

/** Codes after which the current session may have changed (closed elsewhere etc.) → reload it. */
const RELOAD_SESSION_CODES = new Set(['pos.no_open_session', 'pos.session_closed', 'pos.register_inactive']);

/**
 * Till (Kasse) of one register. Built for scanners and keyboard: the code input keeps the focus (scanners type the
 * code + Enter), F2 focuses it, F4 card for the rest, F8 exact cash, F9 pays, Esc cancels step by step.
 */
@Component({
  selector: 'hn-till-page',
  imports: [FormsModule, RouterLink, PosSubnavComponent, ReceiptViewComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './till.page.html',
  host: { '(document:keydown)': 'onKeydown($event)' },
})
export class TillPage {
  private readonly api = inject(PosApi);
  private readonly store = inject(SessionStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly injector = inject(Injector);

  readonly registerId = input.required<string>();

  protected readonly shortcuts = TILL_SHORTCUTS;
  protected readonly lineAmounts = lineAmounts;
  protected readonly maxQuantity = maxQuantity;

  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeField');
  private readonly lastPrices = new LastPrices(sessionStorageOrNull(), this.store.organizationId() ?? 'none');

  // ---- data
  protected readonly register = createQuery(
    () => this.registerId(),
    (id) => this.api.register(id),
  );
  /** Current open session, or null when the register has none. */
  protected readonly sessionQuery = createQuery(
    () => this.registerId(),
    (id) => this.api.currentSession(id),
  );
  protected readonly session = computed(() => this.sessionQuery.value());

  // ---- open session
  protected openingCash = '';
  protected readonly openingError = signal<string | null>(null);

  // ---- cart
  protected code = '';
  protected readonly lines = signal<CartLine[]>([]);
  protected readonly selectedKey = signal<string | null>(null);
  protected readonly totals = computed(() => cartTotals(this.lines()));
  protected readonly scanError = signal<string | null>(null);
  protected readonly lookups = signal(0);
  /** Line the last API error refers to (e.g. stock.insufficient). */
  protected readonly errorLineKey = signal<string | null>(null);

  // ---- payment
  protected readonly cash = signal('');
  protected readonly card = signal('');
  protected readonly settlement = computed(() => settle(this.totals().totalMinor, this.cash(), this.card()));
  protected readonly suggestions = computed(() => cashSuggestions(this.totals().totalMinor));
  protected readonly checkoutError = signal<string | null>(null);
  protected readonly paying = signal(false);
  private pending: PendingAttempt | null = null;

  // ---- after the sale
  protected readonly lastReceipt = signal<Receipt | null>(null);
  protected readonly confirmDiscard = signal(false);

  constructor() {
    // Scanner-ready as soon as the session is known.
    effect(() => {
      if (this.session()?.id) this.focusCode();
    });
  }

  // =============================================================== session
  protected async openSession(): Promise<void> {
    const minor = parseAmount(this.openingCash, null);
    if (minor === null) {
      this.openingError.set(this.i18n.t('pos.till.openingInvalid'));
      return;
    }
    this.openingError.set(null);
    this.paying.set(true);
    try {
      this.sessionQuery.set(await firstValueFrom(this.api.openSession(this.registerId(), minor)));
      this.toast.success(this.i18n.t('pos.till.sessionOpened'));
      this.focusCode();
    } catch (e) {
      this.openingError.set(posErrorText(this.i18n, e));
      if (toApiProblem(e).code === 'pos.session_already_open') this.sessionQuery.reload();
    } finally {
      this.paying.set(false);
    }
  }

  // =============================================================== scanning
  protected async scan(): Promise<void> {
    const code = this.code.trim();
    this.code = '';
    if (!code) return;
    if (this.lastReceipt()) this.lastReceipt.set(null);
    this.scanError.set(null);
    this.lookups.update((n) => n + 1);
    try {
      const found = await firstValueFrom(this.api.lookup(code, this.registerId()));
      const result = addToCart(this.lines(), found, this.lastPrices.get(found.productId));
      if (!result.ok) {
        this.scanError.set(
          result.error === 'notAvailable'
            ? this.i18n.t('pos.errors.insufficientStock', { available: result.available ?? 0 })
            : this.i18n.t('pos.till.addError.' + result.error),
        );
        return;
      }
      this.lines.set(result.lines);
      this.selectedKey.set(result.key);
      this.afterCartChange();
      if (result.needsPrice) this.focusId('pos-price-' + result.key);
      else this.focusCode();
    } catch (e) {
      this.scanError.set(this.i18n.t('pos.till.scanFailed', { code }) + ' ' + posErrorText(this.i18n, e));
    } finally {
      this.lookups.update((n) => n - 1);
    }
  }

  /** "+"/"−" in the empty code field change the quantity of the selected (last scanned) line. */
  protected onCodeKeydown(e: KeyboardEvent): void {
    if (this.code.trim() || (e.key !== '+' && e.key !== '-')) return;
    const key = this.selectedKey() ?? this.lines().at(-1)?.key;
    if (!key) return;
    e.preventDefault();
    this.changeQty(key, e.key === '+' ? 1 : -1, false);
  }

  protected changeQty(key: string, delta: number, refocus = true): void {
    this.lines.set(changeQuantity(this.lines(), key, delta));
    this.selectedKey.set(key);
    this.afterCartChange();
    if (refocus) this.focusCode();
  }

  protected setPrice(key: string, value: string): void {
    this.updateLine(key, { priceInput: value });
  }

  protected setDiscount(key: string, value: string): void {
    this.updateLine(key, { discountInput: value });
  }

  protected removeLine(key: string): void {
    this.lines.set(this.lines().filter((l) => l.key !== key));
    if (this.selectedKey() === key) this.selectedKey.set(this.lines().at(-1)?.key ?? null);
    this.afterCartChange();
    this.focusCode();
  }

  private updateLine(key: string, patch: Partial<CartLine>): void {
    this.lines.set(this.lines().map((l) => (l.key === key ? { ...l, ...patch } : l)));
    this.selectedKey.set(key);
    this.afterCartChange();
  }

  private afterCartChange(): void {
    this.checkoutError.set(null);
    this.errorLineKey.set(null);
  }

  // =============================================================== payment
  protected setCashMinor(minor: number): void {
    this.cash.set(minorToPlain(minor));
    this.checkoutError.set(null);
  }

  protected applyExactCash(): void {
    if (this.totals().totalMinor > 0) this.setCashMinor(exactCash(this.totals().totalMinor, this.card()));
  }

  protected applyCardRest(): void {
    if (this.totals().totalMinor <= 0) return;
    this.card.set(minorToPlain(cardRest(this.totals().totalMinor, this.cash())));
    this.checkoutError.set(null);
  }

  protected async checkout(): Promise<void> {
    if (this.paying() || !this.session()) return;
    const totals = this.totals();
    if (!totals.valid) {
      const invalid = totals.invalidKeys[0];
      this.checkoutError.set(this.i18n.t(this.lines().length ? 'pos.till.fixLines' : 'pos.till.emptyCart'));
      if (invalid) this.focusId('pos-price-' + invalid);
      return;
    }
    const s = this.settlement();
    if (s.error) {
      this.checkoutError.set(this.i18n.t('pos.till.settle.' + s.error));
      this.focusId(s.error === 'cardExceedsTotal' || s.error === 'cashNotNeeded' ? 'pos-card' : 'pos-cash');
      return;
    }
    const body = buildSaleRequest(this.registerId(), this.lines(), s.payments);
    const attempt = attemptFor(this.pending, JSON.stringify(body), newIdempotencyKey);
    this.pending = attempt;
    this.paying.set(true);
    this.checkoutError.set(null);
    try {
      const receipt = await firstValueFrom(this.api.sell(body, attempt.key));
      this.pending = null;
      this.lastPrices.remember(Object.fromEntries(body.lines.filter((l) => l.productId).map((l) => [l.productId!, l.unitPriceMinor])));
      this.lastReceipt.set(receipt);
      this.resetCart();
      this.sessionQuery.reload();
      this.focusCode();
    } catch (e) {
      if (!keepAttemptAfter(problemStatus(e))) this.pending = null;
      const problem = toApiProblem(e);
      this.checkoutError.set(posErrorText(this.i18n, e) + (this.pending ? ' ' + this.i18n.t('pos.till.retryHint') : ''));
      const productId = problemDetail(e, 'productId');
      if (typeof productId === 'string') this.errorLineKey.set(this.lines().find((l) => l.productId === productId && l.kind === 'Product')?.key ?? null);
      if (problem.code && RELOAD_SESSION_CODES.has(problem.code)) {
        this.sessionQuery.reload();
        this.register.reload();
      }
    } finally {
      this.paying.set(false);
    }
  }

  // =============================================================== cancel / new sale
  protected newSale(): void {
    this.lastReceipt.set(null);
    this.focusCode();
  }

  protected discardCart(): void {
    this.confirmDiscard.set(false);
    this.resetCart();
    this.focusCode();
  }

  private resetCart(): void {
    this.lines.set([]);
    this.selectedKey.set(null);
    this.cash.set('');
    this.card.set('');
    this.pending = null;
    this.checkoutError.set(null);
    this.errorLineKey.set(null);
    this.scanError.set(null);
  }

  protected print(): void {
    if (typeof window !== 'undefined') window.print();
    this.focusCode();
  }

  // =============================================================== keyboard
  protected onKeydown(e: KeyboardEvent): void {
    if (!this.session()) return;
    const action = tillShortcut(e);
    if (!action) {
      // A scanner "types" into whatever has the focus: route printable keys to the code field unless another
      // field (price, discount, payment) or a dialog is being used.
      if (isPrintableKey(e) && !isEditable(e.target) && !this.confirmDiscard()) this.codeInput()?.nativeElement.focus();
      return;
    }
    // Native dialogs handle Esc themselves.
    if (this.confirmDiscard()) return;
    if (action !== 'cancel') e.preventDefault();
    switch (action) {
      case 'focusCode':
        this.focusCode();
        break;
      case 'cardRest':
        this.applyCardRest();
        this.focusId('pos-card');
        break;
      case 'cashExact':
        this.applyExactCash();
        this.focusId('pos-cash');
        break;
      case 'pay':
        void this.checkout();
        break;
      case 'cancel':
        this.cancelStep(e);
        break;
    }
  }

  /** Esc: hide receipt → clear code → clear payments → ask to discard the cart. */
  private cancelStep(e: KeyboardEvent): void {
    e.preventDefault();
    if (this.lastReceipt()) return this.newSale();
    if (this.code) {
      this.code = '';
      this.scanError.set(null);
      return this.focusCode();
    }
    if (this.cash() || this.card()) {
      this.cash.set('');
      this.card.set('');
      this.checkoutError.set(null);
      return this.focusCode();
    }
    if (this.lines().length) this.confirmDiscard.set(true);
    else this.focusCode();
  }

  /** Clicks on empty till areas return the focus to the code input (scanner-ready). */
  protected onSurfaceClick(e: MouseEvent): void {
    const target = e.target as HTMLElement | null;
    if (target?.closest('input, button, a, select, textarea, label, [tabindex]')) return;
    if (window.getSelection()?.toString()) return;
    this.focusCode();
  }

  protected focusCode(): void {
    afterNextRender(() => this.codeInput()?.nativeElement.focus(), { injector: this.injector });
  }

  /** Price input: Enter goes back to scanning. */
  protected priceEnter(e: Event): void {
    e.preventDefault();
    this.focusCode();
  }

  private focusId(id: string): void {
    afterNextRender(
      () => {
        const el = document.getElementById(id) as HTMLInputElement | null;
        el?.focus();
        el?.select?.();
      },
      { injector: this.injector },
    );
  }

}

function sessionStorageOrNull(): Storage | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage;
  } catch {
    return null;
  }
}

function isPrintableKey(e: KeyboardEvent): boolean {
  return e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey;
}

function isEditable(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  return !!el?.closest?.('input, textarea, select, [contenteditable="true"], dialog');
}
