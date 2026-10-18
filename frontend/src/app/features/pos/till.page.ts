import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  TemplateRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api/api-problem';
import { PosApi } from '../../core/api/handynet-api';
import { PosCustomer, PosLookup, PosPaymentMethod, PosPrice, PosProduct, Receipt } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { PosCashierStore } from './pos-cashier.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { formatMoney } from '../../core/format/money';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { CashCounts, countTotal, toDenominations } from './cash-count';
import { posErrorText } from './pos-error';
import { PosFiscalStatus } from './pos-fiscal-status';
import { PosCashCountComponent } from './pos-cash-count';
import { PosContext } from './pos-context';
import { PosEReceiptComponent } from './pos-ereceipt';
import { customerSummary } from './customer-form';
import { PosCustomerDialogComponent } from './pos-customer-dialog';
import { PosNumpadComponent } from './pos-numpad';
import { PosPriceDialogComponent, PriceTarget } from './pos-price-dialog';
import { PosProductGridComponent } from './pos-product-grid';
import { LinePolicyIssue, approvalPermissionsFor, canEditPrice, canGiveDiscount, linePolicyIssue, posRights } from './pos-rights';
import {
  CartLine,
  LastPrices,
  NumpadKey,
  NumpadMode,
  OrdersState,
  OrdersStorage,
  POS_LIMITS,
  PaymentLine,
  TILL_SHORTCUTS,
  activeOrder,
  addOrder,
  addPaymentLine,
  addToCart,
  applyNumpad,
  attemptFor,
  buildSaleRequest,
  cartTax,
  cartTotals,
  cashSuggestions,
  changeQuantity,
  initialOrders,
  keepAttemptAfter,
  lineAmounts,
  looksLikeCode,
  lookupFromProduct,
  maxQuantity,
  minorToPlain,
  newIdempotencyKey,
  numpadValue,
  orderItemCount,
  parseAmount,
  pressNumpadKey,
  problemDetail,
  problemStatus,
  remainingFor,
  removeOrder,
  sanitizeNumpadText,
  setLineNote,
  settlePayments,
  tillShortcut,
  updateOrder,
} from './pos.logic';
import { ReceiptViewComponent } from './receipt-view';

/** Codes after which the current session may have changed (closed elsewhere etc.) → reload it. */
const RELOAD_SESSION_CODES = new Set(['pos.no_open_session', 'pos.session_closed', 'pos.register_inactive']);

type Screen = 'products' | 'payment' | 'receipt';

/**
 * Selling screen of one register, modelled on Odoo POS: cart with numpad (Qty / % Disc / Price) on one side,
 * category tabs and product tiles plus the search/scan field on the other, several open tickets (parked orders,
 * stored per register in localStorage), a payment screen with split payments and a receipt screen.
 * Built for scanners and keyboards: the code field keeps the focus (scanners type the code + Enter), F2 focuses it,
 * F4 card for the rest, F8 exact cash, F9 payment / validate, Esc goes back step by step.
 */
@Component({
  selector: 'hn-till-page',
  imports: [
    FormsModule,
    PosCashCountComponent,
    PosCustomerDialogComponent,
    PosNumpadComponent,
    PosPriceDialogComponent,
    PosProductGridComponent,
    ReceiptViewComponent,
    PosEReceiptComponent,
    ...UI,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './till.page.html',
  host: { class: 'block h-full', '(document:keydown)': 'onKeydown($event)' },
})
export class TillPage {
  private readonly api = inject(PosApi);
  private readonly store = inject(SessionStore);
  protected readonly cashiers = inject(PosCashierStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly injector = inject(Injector);
  protected readonly ctx = inject(PosContext);
  private readonly fiscalStatus = inject(PosFiscalStatus, { optional: true });

  readonly registerId = input.required<string>();

  protected readonly shortcuts = TILL_SHORTCUTS;
  protected readonly lineAmounts = lineAmounts;
  protected readonly maxQuantity = maxQuantity;
  protected readonly itemCount = orderItemCount;
  protected readonly hasInventory = computed(() => this.store.hasModule('Inventory'));
  /** Per-cashier rights (price override, discount limit, …) of the selected membership. */
  protected readonly rights = computed(() => posRights(this.cashiers.membership()));
  protected readonly canDiscount = computed(() => canGiveDiscount(this.rights()));
  protected readonly noteMax = POS_LIMITS.lineNoteLength;
  protected readonly customerSummary = customerSummary;

  private readonly codeInput = viewChild<ElementRef<HTMLInputElement>>('codeField');
  private readonly grid = viewChild(PosProductGridComponent);
  private readonly ticketsTpl = viewChild<TemplateRef<unknown>>('ticketsTpl');
  private readonly lastPrices = new LastPrices(browserStorage('session'), this.store.organizationId() ?? 'none');

  // ---- data
  protected readonly register = createQuery(
    () => this.registerId(),
    (id) => this.api.register(id),
  );
  /** Current open session, or null when the register has none. */
  protected readonly sessionQuery = createQuery(
    () => this.registerId(),
    (id) => this.api.currentSession(id).pipe(finalize(() => this.sessionAnswered.set(true))),
  );
  protected readonly session = computed(() => this.sessionQuery.value());
  private readonly sessionAnswered = signal(false);
  /** False until the first answer about the register's session arrived (no flash of the opening form). */
  protected readonly sessionKnown = computed(() => this.sessionAnswered() && !this.sessionQuery.error());

  // ---- open session (plain amount or counted by denomination)
  protected openingCash = '';
  protected readonly openingMode = signal<'amount' | 'count'>('amount');
  protected readonly openingCounts = signal<CashCounts>({});
  protected readonly openingError = signal<string | null>(null);
  protected readonly opening = signal(false);

  // ---- tickets (parked orders), persisted per organization + register
  private readonly ordersStorage = computed(() => new OrdersStorage(browserStorage('local'), `${this.store.organizationId() ?? 'none'}.${this.registerId()}`));
  protected readonly orders = linkedSignal<OrdersStorage, OrdersState>({
    source: () => this.ordersStorage(),
    computation: (storage) => storage.load() ?? initialOrders(newIdempotencyKey(), new Date().toISOString()),
  });
  protected readonly order = computed(() => activeOrder(this.orders()));
  protected readonly lines = computed(() => this.order().lines);
  protected readonly totals = computed(() => cartTotals(this.lines()));
  protected readonly tax = computed(() => cartTax(this.lines()));
  protected readonly customer = computed(() => this.order().customer ?? null);
  /** Lines the cashier may not sell like this (price override, discount right/limit), by key. */
  /** Rights a manager must approve for the cart as it is (price change, discount beyond the cashier's rights). */
  protected readonly approvalNeeded = computed(() => approvalPermissionsFor([...this.issues().values()]));
  protected readonly issues = computed(() => {
    const rights = this.rights();
    const out = new Map<string, LinePolicyIssue>();
    for (const l of this.lines()) {
      const a = lineAmounts(l);
      const issue = a.error ? null : linePolicyIssue({ ...a, quantity: l.quantity, listPriceMinor: l.listPriceMinor }, rights);
      if (issue) out.set(l.key, issue);
    }
    return out;
  });

  // ---- product area / scanning
  protected readonly code = signal('');
  protected readonly scanError = signal<string | null>(null);
  protected readonly lookups = signal(0);
  /** Below the md breakpoint only one pane is visible. */
  protected readonly pane = signal<'products' | 'cart'>('products');

  // ---- selected line + numpad
  protected readonly selectedKey = signal<string | null>(null);
  protected readonly selectedLine = computed(() => {
    const key = this.selectedKey();
    return this.lines().find((l) => l.key === key) ?? this.lines().at(-1) ?? null;
  });
  private readonly selectedLineKey = computed(() => this.selectedLine()?.key ?? null);
  protected readonly priceEditable = computed(() => {
    const line = this.selectedLine();
    return !!line && canEditPrice(line, this.rights());
  });
  protected readonly mode = signal<NumpadMode>('quantity');
  protected readonly buffer = signal('');
  private fresh = true;
  /** Line the last API error refers to (e.g. stock.insufficient). */
  protected readonly errorLineKey = signal<string | null>(null);

  // ---- payment
  protected readonly screen = signal<Screen>('products');
  protected readonly payments = computed(() => this.order().payments);
  protected readonly selectedPaymentId = signal<string | null>(null);
  protected readonly selectedPayment = computed(() => this.payments().find((p) => p.id === this.selectedPaymentId()) ?? null);
  private payFresh = true;
  protected readonly settlement = computed(() => settlePayments(this.totals().totalMinor, this.payments(), this.register.value()?.cashRounding ?? false));
  /** Next banknotes for the amount the cash line has to cover. */
  protected readonly cashQuick = computed(() => {
    const cashLine = this.payments().find((p) => p.method === 'Cash');
    return cashSuggestions(remainingFor(this.totals().totalMinor, this.payments(), cashLine?.id ?? null), 5);
  });
  protected readonly checkoutError = signal<string | null>(null);
  protected readonly paying = signal(false);
  protected readonly methods: PosPaymentMethod[] = ['Cash', 'Card'];

  // ---- after the sale
  protected readonly lastReceipt = signal<Receipt | null>(null);
  protected readonly confirmDiscard = signal(false);

  // ---- dialogs: line note, customer, price (pos.manage)
  protected readonly noteOpen = signal(false);
  protected noteText = '';
  protected readonly customerOpen = signal(false);
  protected readonly priceTarget = signal<PriceTarget | null>(null);
  private readonly dialogOpen = computed(() => this.confirmDiscard() || this.noteOpen() || this.customerOpen() || !!this.priceTarget());

  constructor() {
    effect(() => {
      const name = this.register.value()?.name ?? null;
      untracked(() => this.ctx.setRegister(this.registerId(), name));
    });
    effect(() => {
      const known = this.sessionKnown();
      const s = this.session();
      untracked(() => this.ctx.session.set(known ? s : undefined));
    });
    // Parked tickets live in the top bar while a session is open.
    effect(() => {
      const tpl = this.session() ? (this.ticketsTpl() ?? null) : null;
      untracked(() => this.ctx.topbarTickets.set(tpl));
    });
    inject(DestroyRef).onDestroy(() => this.ctx.topbarTickets.set(null));
    effect(() => {
      const state = this.orders();
      untracked(() => this.ordersStorage().save(state));
    });
    // Scanner-ready as soon as the session is known.
    effect(() => {
      if (this.session()?.id) untracked(() => this.focusCode());
    });
    // Selecting another line or mode shows that value on the numpad; the next key replaces it (not on every edit).
    effect(() => {
      this.selectedLineKey();
      const mode = this.mode();
      untracked(() => {
        const line = this.selectedLine();
        if (line && !this.modeAllowed(line, mode)) {
          this.mode.set('quantity');
          return;
        }
        this.buffer.set(line ? numpadValue(line, mode) : '');
        this.fresh = true;
      });
    });
  }

  // =============================================================== session
  protected async openSession(): Promise<void> {
    const counted = this.openingMode() === 'count';
    const minor = counted ? countTotal(this.openingCounts()) : parseAmount(this.openingCash, null);
    if (minor === null) {
      this.openingError.set(this.i18n.t('pos.till.openingInvalid'));
      return;
    }
    this.openingError.set(null);
    this.opening.set(true);
    try {
      const body = counted ? { denominations: toDenominations(this.openingCounts()) } : { openingCashMinor: minor };
      this.sessionQuery.set(await firstValueFrom(this.api.openSession(this.registerId(), body)));
      this.toast.success(this.i18n.t('pos.till.sessionOpened'));
      this.focusCode();
    } catch (e) {
      this.openingError.set(posErrorText(this.i18n, e));
      if (toApiProblem(e).code === 'pos.session_already_open') this.sessionQuery.reload();
    } finally {
      this.opening.set(false);
    }
  }

  // =============================================================== tickets
  private setLines(lines: CartLine[]): void {
    const order = this.order();
    // A pending checkout attempt stays: attemptFor() only reuses its key for an identical request body.
    this.orders.update((s) => updateOrder(s, order.id, { lines }));
    this.checkoutError.set(null);
    this.errorLineKey.set(null);
  }

  private setPayments(payments: PaymentLine[]): void {
    const order = this.order();
    this.orders.update((s) => updateOrder(s, order.id, { payments }));
    this.checkoutError.set(null);
  }

  protected newOrder(): void {
    const next = addOrder(this.orders(), newIdempotencyKey(), new Date().toISOString());
    if (!next) {
      this.scanError.set(this.i18n.t('pos.sell.tooManyOrders'));
      return;
    }
    this.orders.set(next);
    this.afterOrderSwitch();
  }

  protected switchOrder(id: string): void {
    if (id === this.order().id) return;
    this.orders.update((s) => ({ ...s, activeId: id }));
    this.afterOrderSwitch();
  }

  private afterOrderSwitch(): void {
    this.selectedKey.set(this.lines().at(-1)?.key ?? null);
    this.mode.set('quantity');
    this.screen.set('products');
    this.lastReceipt.set(null);
    this.scanError.set(null);
    this.checkoutError.set(null);
    this.errorLineKey.set(null);
    this.code.set('');
    this.focusCode();
  }

  protected discardOrder(): void {
    this.confirmDiscard.set(false);
    this.orders.set(removeOrder(this.orders(), this.order().id, newIdempotencyKey(), new Date().toISOString()));
    this.afterOrderSwitch();
  }

  // =============================================================== scanning / products
  protected onCodeInput(value: string): void {
    this.code.set(value);
    if (this.scanError()) this.scanError.set(null);
  }

  /** Enter in the search/scan field: EAN/IMEI → lookup; text with exactly one matching product → add it. */
  protected async submitCode(): Promise<void> {
    const text = this.code().trim();
    if (!text) return;
    if (this.screen() === 'receipt') this.startNextOrder();
    if (!looksLikeCode(text)) {
      const match = this.grid()?.singleMatch();
      if (match) {
        this.code.set('');
        this.pickProduct(match);
      }
      return;
    }
    this.code.set('');
    await this.lookupAndAdd(text);
  }

  /** A tapped tile: the grid row carries stock, the IMEI requirement and the till price. */
  protected pickProduct(p: PosProduct): void {
    if (this.screen() === 'receipt') this.startNextOrder();
    this.scanError.set(null);
    if (p.available <= 0) {
      this.scanError.set(this.i18n.t('pos.errors.insufficientStock', { available: p.available }));
      return;
    }
    this.addLookup(lookupFromProduct(p));
  }

  private async lookupAndAdd(code: string): Promise<void> {
    this.scanError.set(null);
    this.lookups.update((n) => n + 1);
    try {
      this.addLookup(await firstValueFrom(this.api.lookup(code, this.registerId())));
    } catch (e) {
      this.scanError.set(this.i18n.t('pos.till.scanFailed', { code }) + ' ' + posErrorText(this.i18n, e));
    } finally {
      this.lookups.update((n) => n - 1);
    }
  }

  private addLookup(found: PosLookup): void {
    const result = addToCart(this.lines(), found, this.lastPrices.get(found.productId));
    if (!result.ok) {
      this.scanError.set(
        result.error === 'notAvailable'
          ? this.i18n.t('pos.errors.insufficientStock', { available: result.available ?? 0 })
          : this.i18n.t('pos.till.addError.' + result.error),
      );
      return;
    }
    const isNew = result.lines.length > this.lines().length;
    this.setLines(result.lines);
    this.selectedKey.set(result.key);
    if (result.needsPrice) {
      this.mode.set('price');
      this.focusNumpad();
    } else {
      if (isNew) this.mode.set('quantity');
      this.focusCode();
    }
  }

  // =============================================================== cart lines + numpad
  protected selectLine(key: string): void {
    this.selectedKey.set(key);
    this.fresh = true;
    this.buffer.set(numpadValue(this.selectedLine()!, this.mode()));
  }

  protected setMode(mode: NumpadMode): void {
    const line = this.selectedLine();
    if (mode === 'price' && line && !canEditPrice(line, this.rights())) return;
    if (mode === 'discount' && !this.canDiscount()) return;
    this.mode.set(mode);
    this.buffer.set(line ? numpadValue(line, mode) : '');
    this.fresh = true;
    this.focusNumpad();
  }

  protected onNumpadKey(key: NumpadKey): void {
    const line = this.selectedLine();
    if (!line) return;
    const mode = this.mode();
    if (mode === 'quantity' && line.kind === 'Unit' && key !== 'backspace') {
      this.scanError.set(this.i18n.t('pos.sell.unitQuantity'));
      return;
    }
    if (!this.modeAllowed(line, mode) && key !== 'backspace') return;
    const r = pressNumpadKey({ buffer: this.buffer(), fresh: this.fresh }, key, mode);
    if (r.remove) {
      this.removeLine(line.key);
      return;
    }
    this.applyBuffer(line, r.buffer);
  }

  /** Keyboard input in the numpad display field. */
  protected onNumpadText(el: HTMLInputElement): void {
    const line = this.selectedLine();
    if (!line) return;
    if (!this.modeAllowed(line, this.mode())) {
      el.value = this.buffer();
      return;
    }
    const clean = sanitizeNumpadText(el.value, this.mode());
    if (clean !== el.value) el.value = clean;
    this.applyBuffer(line, clean);
  }

  private applyBuffer(line: CartLine, buffer: string): void {
    this.buffer.set(buffer);
    this.fresh = false;
    const applied = applyNumpad(this.lines(), line.key, this.mode(), buffer);
    this.setLines(applied.lines);
    this.scanError.set(applied.clamped ? this.i18n.t('pos.sell.quantityClamped', { max: maxQuantity(line) }) : null);
  }

  /** Price changes need `pos.price_override` when the till knows a price; discounts need `pos.discount`. */
  private modeAllowed(line: CartLine, mode: NumpadMode): boolean {
    if (mode === 'price') return canEditPrice(line, this.rights());
    if (mode === 'discount') return this.canDiscount();
    return true;
  }

  // =============================================================== line note
  protected openNote(): void {
    const line = this.selectedLine();
    if (!line) return;
    this.noteText = line.note ?? '';
    this.noteOpen.set(true);
    this.focusId('pos-note-text');
  }

  protected saveNote(): void {
    const line = this.selectedLine();
    if (line) this.setLines(setLineNote(this.lines(), line.key, this.noteText));
    this.closeNote();
  }

  protected closeNote(): void {
    this.noteOpen.set(false);
    this.focusCode();
  }

  // =============================================================== customer
  protected attachCustomer(c: PosCustomer): void {
    const order = this.order();
    this.orders.update((s) => updateOrder(s, order.id, { customer: { id: c.id, name: c.name, company: c.company } }));
    this.customerOpen.set(false);
    this.checkoutError.set(null);
    this.focusCode();
  }

  protected detachCustomer(): void {
    const order = this.order();
    this.orders.update((s) => updateOrder(s, order.id, { customer: null }));
    this.customerOpen.set(false);
    this.focusCode();
  }

  protected closeCustomer(): void {
    this.customerOpen.set(false);
    if (this.screen() === 'products') this.focusCode();
  }

  // =============================================================== prices (pos.manage)
  protected editPrice(p: PosProduct): void {
    this.priceTarget.set({ productId: p.productId, brand: p.brand, name: p.name });
  }

  protected closePrice(): void {
    this.priceTarget.set(null);
    this.focusCode();
  }

  /** New till price: tiles reload; open lines of the product follow it when they still had the old price. */
  protected onPriceSaved(price: PosPrice): void {
    this.grid()?.reload();
    const next = price.priceMinor;
    for (const order of this.orders().orders) {
      const lines = order.lines.map((l) => {
        if (l.productId !== price.productId || l.kind !== 'Product') return l;
        const unchanged = l.listPriceMinor !== null && l.listPriceMinor !== undefined && lineAmounts(l).unitPriceMinor === l.listPriceMinor;
        return {
          ...l,
          listPriceMinor: next,
          priceSource: price.priceSource,
          priceInput: unchanged && next !== null ? minorToPlain(next) : l.priceInput,
        };
      });
      this.orders.update((s) => updateOrder(s, order.id, { lines }));
    }
  }

  protected changeQty(delta: number): void {
    const line = this.selectedLine();
    if (!line) return;
    this.setLines(changeQuantity(this.lines(), line.key, delta));
    if (this.mode() === 'quantity') {
      this.buffer.set(String(this.lines().find((l) => l.key === line.key)?.quantity ?? line.quantity));
      this.fresh = true;
    }
  }

  protected removeLine(key: string): void {
    const index = this.lines().findIndex((l) => l.key === key);
    const rest = this.lines().filter((l) => l.key !== key);
    this.setLines(rest);
    this.selectedKey.set(rest[Math.min(index, rest.length - 1)]?.key ?? null);
    this.scanError.set(null);
    this.focusCode();
  }

  // =============================================================== payment
  protected openPayment(): boolean {
    const totals = this.totals();
    if (!this.lines().length) {
      this.checkoutError.set(this.i18n.t('pos.till.emptyCart'));
      return false;
    }
    if (!totals.valid) {
      const invalid = this.lines().find((l) => totals.invalidKeys.includes(l.key));
      this.checkoutError.set(this.i18n.t('pos.till.fixLines'));
      if (invalid) {
        this.selectedKey.set(invalid.key);
        this.setMode(lineAmounts(invalid).error === 'price' ? 'price' : 'discount');
      }
      this.pane.set('cart');
      return false;
    }
    // Cashier rights (the API answers 403 otherwise): show the first offending line.
    const blocked = this.lines().find((l) => this.issues().has(l.key));
    if (blocked) {
      this.checkoutError.set(this.issueText(this.issues().get(blocked.key)!));
      this.selectedKey.set(blocked.key);
      this.errorLineKey.set(blocked.key);
      this.pane.set('cart');
      return false;
    }
    this.checkoutError.set(null);
    this.screen.set('payment');
    if (!this.selectedPayment()) this.selectedPaymentId.set(this.payments().at(-1)?.id ?? null);
    this.payFresh = true;
    return true;
  }

  protected backToProducts(): void {
    this.screen.set('products');
    this.checkoutError.set(null);
    this.focusCode();
  }

  protected addPayment(method: PosPaymentMethod): void {
    const id = newIdempotencyKey();
    this.setPayments(addPaymentLine(this.payments(), method, this.totals().totalMinor, id));
    this.selectedPaymentId.set(id);
    this.payFresh = true;
    this.focusId('pos-pay-' + id);
  }

  protected selectPayment(id: string): void {
    this.selectedPaymentId.set(id);
    this.payFresh = true;
  }

  protected removePayment(id: string): void {
    const rest = this.payments().filter((p) => p.id !== id);
    this.setPayments(rest);
    if (this.selectedPaymentId() === id) this.selectedPaymentId.set(rest.at(-1)?.id ?? null);
    this.payFresh = true;
  }

  protected setPaymentInput(id: string, el: HTMLInputElement): void {
    const clean = sanitizeNumpadText(el.value, 'amount');
    if (clean !== el.value) el.value = clean;
    this.selectedPaymentId.set(id);
    this.payFresh = false;
    this.setPayments(this.payments().map((p) => (p.id === id ? { ...p, input: clean } : p)));
  }

  protected onPayKey(key: NumpadKey): void {
    let target = this.selectedPayment();
    if (!target) {
      // Typing without a payment line starts a cash payment (like Odoo), empty so the digits form the amount.
      const id = newIdempotencyKey();
      this.setPayments([...this.payments(), { id, method: 'Cash', input: '' }]);
      this.selectedPaymentId.set(id);
      this.payFresh = false;
      target = this.selectedPayment()!;
    }
    const r = pressNumpadKey({ buffer: target.input, fresh: this.payFresh }, key, 'amount');
    this.payFresh = false;
    this.setPayments(this.payments().map((p) => (p.id === target!.id ? { ...p, input: r.buffer } : p)));
  }

  /** +10/+20/+50 € on the selected payment (a new cash payment when none is selected). */
  protected addToPayment(minor: number): void {
    const target = this.selectedPayment();
    if (!target) return this.setCash(minor);
    const current = parseAmount(target.input.replace(/,$/, ''), 0) ?? 0;
    this.payFresh = true;
    this.setPayments(this.payments().map((p) => (p.id === target.id ? { ...p, input: minorToPlain(current + minor) } : p)));
  }

  /** Sets the cash payment (selected cash line, else the first cash line, else a new one) to an amount. */
  protected setCash(minor: number): void {
    const selected = this.selectedPayment();
    const cash = selected?.method === 'Cash' ? selected : this.payments().find((p) => p.method === 'Cash');
    if (cash) {
      this.setPayments(this.payments().map((p) => (p.id === cash.id ? { ...p, input: minorToPlain(minor) } : p)));
      this.selectedPaymentId.set(cash.id);
    } else {
      const id = newIdempotencyKey();
      this.setPayments([...this.payments(), { id, method: 'Cash', input: minorToPlain(minor) }]);
      this.selectedPaymentId.set(id);
    }
    this.payFresh = true;
  }

  /** F8 / "Bar passend": cash covers exactly what the other payments leave open. */
  protected exactCash(): void {
    if (this.screen() !== 'payment' && !this.openPayment()) return;
    const cash = this.selectedPayment()?.method === 'Cash' ? this.selectedPayment() : this.payments().find((p) => p.method === 'Cash');
    const rest = remainingFor(this.totals().totalMinor, this.payments(), cash?.id ?? null);
    if (rest > 0) this.setCash(rest);
  }

  /** F4 / "Karte (Rest)": card for what the other payments leave open (never more than the total). */
  protected cardRest(): void {
    if (this.screen() !== 'payment' && !this.openPayment()) return;
    const card = this.payments().find((p) => p.method === 'Card');
    const rest = remainingFor(this.totals().totalMinor, this.payments(), card?.id ?? null);
    if (rest <= 0) return;
    if (card) {
      this.setPayments(this.payments().map((p) => (p.id === card.id ? { ...p, input: minorToPlain(rest) } : p)));
      this.selectedPaymentId.set(card.id);
      this.payFresh = true;
    } else {
      this.addPayment('Card');
    }
  }

  protected async validate(): Promise<void> {
    if (this.paying() || !this.session()) return;
    if (!this.totals().valid) {
      this.openPayment();
      return;
    }
    const s = this.settlement();
    if (s.error) {
      this.checkoutError.set(this.i18n.t('pos.till.settle.' + s.error));
      return;
    }
    const order = this.order();
    if (this.lines().some((l) => this.issues().has(l.key))) {
      this.backToProducts();
      this.openPayment();
      return;
    }
    const body = buildSaleRequest(this.registerId(), order.lines, s.payments, order.customer?.id ?? null);
    const attempt = attemptFor(order.pending, JSON.stringify(body), newIdempotencyKey);
    this.orders.update((st) => updateOrder(st, order.id, { pending: attempt }));
    this.paying.set(true);
    this.checkoutError.set(null);
    try {
      const receipt = await firstValueFrom(this.api.sell(body, attempt.key));
      this.lastPrices.remember(Object.fromEntries(body.lines.filter((l) => l.productId).map((l) => [l.productId!, l.unitPriceMinor])));
      this.lastReceipt.set(receipt);
      void this.fiscalStatus?.refresh(); // a signed receipt ends a TSE outage; a failed one starts it
      this.finishOrder(order.id);
      this.screen.set('receipt');
      this.sessionQuery.reload();
      this.grid()?.reload();
      this.focusId('pos-new-sale');
    } catch (e) {
      const keep = keepAttemptAfter(problemStatus(e));
      if (!keep) this.orders.update((st) => updateOrder(st, order.id, { pending: null }));
      const problem = toApiProblem(e);
      this.checkoutError.set(posErrorText(this.i18n, e) + (keep ? ' ' + this.i18n.t('pos.till.retryHint') : ''));
      const productId = problemDetail(e, 'productId');
      const lineNumber = problemDetail(e, 'lineNumber');
      if (typeof productId === 'string') {
        this.errorLineKey.set(order.lines.find((l) => l.productId === productId && l.kind === 'Product')?.key ?? null);
      } else if (typeof lineNumber === 'number') {
        this.errorLineKey.set(order.lines[lineNumber - 1]?.key ?? null);
      }
      if (problem.code && RELOAD_SESSION_CODES.has(problem.code)) {
        this.sessionQuery.reload();
        this.register.reload();
      }
    } finally {
      this.paying.set(false);
    }
  }

  /** The sold ticket is removed; selling continues on a fresh ticket (parked ones stay parked). */
  private finishOrder(id: string): void {
    const now = new Date().toISOString();
    let next = removeOrder(this.orders(), id, newIdempotencyKey(), now);
    if (activeOrder(next).lines.length) next = addOrder(next, newIdempotencyKey(), now) ?? next;
    this.orders.set(next);
    this.selectedKey.set(null);
    this.selectedPaymentId.set(null);
    this.mode.set('quantity');
  }

  /** Text for a line the cashier's rights do not allow. */
  protected issueText(issue: LinePolicyIssue): string {
    switch (issue.kind) {
      case 'priceOverride':
        return this.i18n.t('pos.rights.priceLocked', { price: formatMoney(issue.expectedMinor) });
      case 'discountForbidden':
        return this.i18n.t('pos.rights.discountForbidden');
      case 'discountLimit':
        return this.i18n.t('pos.rights.discountLimit', { percent: issue.maxPercent, amount: formatMoney(issue.maxDiscountMinor) });
    }
  }

  // =============================================================== receipt
  protected startNextOrder(): void {
    this.lastReceipt.set(null);
    this.screen.set('products');
    this.focusCode();
  }

  protected print(): void {
    if (typeof window !== 'undefined') window.print();
  }

  // =============================================================== keyboard
  protected onKeydown(e: KeyboardEvent): void {
    if (!this.session() || this.dialogOpen()) return;
    const action = tillShortcut(e);
    if (!action) {
      if (!isPrintableKey(e) || isEditable(e.target)) return;
      if (this.screen() === 'payment') {
        // Keyboard digits feed the payment numpad.
        if (/^[0-9,.]$/.test(e.key)) {
          e.preventDefault();
          this.onPayKey((e.key === '.' ? ',' : e.key) as NumpadKey);
        }
        return;
      }
      // A scanner "types" into whatever has the focus: route printable keys to the code field.
      if (this.screen() === 'receipt') this.startNextOrder();
      this.codeInput()?.nativeElement.focus();
      return;
    }
    if (action !== 'cancel') e.preventDefault();
    switch (action) {
      case 'focusCode':
        if (this.screen() !== 'products') this.backToProducts();
        else this.focusCode();
        break;
      case 'cardRest':
        this.cardRest();
        break;
      case 'cashExact':
        this.exactCash();
        break;
      case 'pay':
        if (this.screen() === 'payment') void this.validate();
        else if (this.screen() === 'products') this.openPayment();
        break;
      case 'cancel':
        this.cancelStep(e);
        break;
    }
  }

  /** Esc: receipt → next order; payment → products; clear the code; ask to discard the ticket. */
  private cancelStep(e: KeyboardEvent): void {
    if (isInsideDialog(e.target)) return;
    e.preventDefault();
    if (this.screen() === 'receipt') return this.startNextOrder();
    if (this.screen() === 'payment') return this.backToProducts();
    if (this.code()) {
      this.code.set('');
      this.scanError.set(null);
      return this.focusCode();
    }
    if (this.lines().length) this.confirmDiscard.set(true);
    else this.focusCode();
  }

  /** Numpad display: Enter / Esc go back to scanning. */
  protected numpadEnter(e: Event): void {
    e.preventDefault();
    e.stopPropagation();
    this.focusCode();
  }

  protected focusCode(): void {
    afterNextRender(() => this.codeInput()?.nativeElement.focus(), { injector: this.injector });
  }

  private focusNumpad(): void {
    this.focusId('pos-numpad-input');
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

function browserStorage(kind: 'local' | 'session'): Storage | null {
  try {
    if (kind === 'local') return typeof localStorage === 'undefined' ? null : localStorage;
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
  return !!el?.closest?.('input, textarea, select, [contenteditable="true"], dialog, [role="menu"]');
}

function isInsideDialog(target: EventTarget | null): boolean {
  return !!(target as HTMLElement | null)?.closest?.('dialog');
}
