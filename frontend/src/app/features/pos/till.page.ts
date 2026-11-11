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
import { PosCustomer, PosLookup, PosPaymentMethod, PosPrice, PosProduct, Receipt, SalePayment } from '../../core/api/models';
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
  giftCardLine,
  giftCardLinesMinor,
  loyaltyEligibleMinor,
} from './pos.logic';
import { ReceiptViewComponent } from './receipt-view';
import { PrinterService } from './printing/printer.service';
import { CompanyBrandingStore } from '../../core/company/company-branding.store';
import { DisplayPublisher } from './display/display-publisher';
import { displayStateFromTill, idleDisplayState, messageFromReceiptText } from './display/display-state';
import { afterSaleFailure, cardAmount, cardLabel, cardStep, withCardTransaction } from './terminals/card-flow';
import { TerminalService } from './terminals/terminal.service';
import { OfflineLookupError, PosOfflineData } from './offline/offline-data.service';
import { PaymentLinesSettlementLike, QueueEntry, offlineBlock } from './offline/offline-queue.logic';
import { OfflineQueueService } from './offline/offline-queue.service';
import { ProvisionalReceiptComponent, provisionalLayout } from './offline/provisional-receipt';
import { ReachabilityService, isUnreachable } from './offline/reachability.service';
import { GiftCardSlipsComponent } from './giftcards/gift-card-slips';
import { GiftCardPaymentChoice, PosGiftCardPayDialogComponent, PosGiftCardSaleDialogComponent } from './giftcards/pos-gift-card-dialogs';
import { IssuedGiftCard, maskedGiftCard } from './giftcards/gift-card.logic';
import { CustomerLoyalty, LoyaltyApi, LoyaltySettings } from './loyalty/loyalty-api';
import { maxRedeemPoints, parsePoints, pointsEarned, redeemError, redeemValueMinor } from './loyalty/loyalty.logic';
import { ResolvedPrice, repriceLines, repriceTargets } from './pricelists/pricing.logic';
import { catchError, forkJoin, map, of } from 'rxjs';
import { germanT } from './printing/printer.service';
import { receiptLines } from '../company/company.logic';

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
    ProvisionalReceiptComponent,
    GiftCardSlipsComponent,
    PosGiftCardSaleDialogComponent,
    PosGiftCardPayDialogComponent,
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
  private readonly printer = inject(PrinterService);
  protected readonly ctx = inject(PosContext);
  private readonly fiscalStatus = inject(PosFiscalStatus, { optional: true });
  private readonly display = inject(DisplayPublisher);
  private readonly terminals = inject(TerminalService);
  private readonly offlineData = inject(PosOfflineData);
  private readonly queue = inject(OfflineQueueService);
  private readonly reach = inject(ReachabilityService);
  private readonly loyaltyApi = inject(LoyaltyApi);
  private readonly branding = inject(CompanyBrandingStore);

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
  private readonly cartOpener = viewChild<ElementRef<HTMLButtonElement>>('cartOpener');
  private readonly grid = viewChild(PosProductGridComponent);
  private readonly ticketsTpl = viewChild<TemplateRef<unknown>>('ticketsTpl');
  private readonly lastPrices = new LastPrices(browserStorage('session'), this.store.organizationId() ?? 'none');

  // ---- data
  protected readonly register = createQuery(
    () => this.registerId(),
    (id) => this.offlineData.register(id),
  );
  /** Current open session, or null when the register has none (offline: the session open when the API was last reached). */
  protected readonly sessionQuery = createQuery(
    () => this.registerId(),
    (id) => this.offlineData.currentSession(id).pipe(finalize(() => this.sessionAnswered.set(true))),
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
  /**
   * Layout by width (Odoo POS style):
   * - below md (< 768 px, phones): the product grid fills the screen with a sticky bottom bar (items, total, cart, pay);
   *   `pane() === 'cart'` shows the cart as a full-height sheet over it;
   * - from md (≥ 768 px): products and cart side by side. Tablet portrait (768–1024 px, e.g. 820 × 1180) keeps the split
   *   on purpose: the cart pane (19–24 rem) plus a 2–3 column product grid fit, and the tall screen leaves room for the
   *   cart lines above the numpad, so the cashier never has to open/close a sheet on the primary till device.
   */
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
  // ---- loyalty points and gift cards on the ticket
  /** Settings of the organization (null until loaded or when the API is not reachable). */
  protected readonly loyaltySettings = signal<LoyaltySettings | null>(null);
  /** Points of the ticket's customer as the API reported them last. */
  protected readonly customerLoyalty = signal<CustomerLoyalty | null>(null);
  protected readonly loyaltyOn = computed(() => !!this.loyaltySettings()?.enabled);
  protected readonly redeemPoints = computed(() => this.order().redeemPoints ?? 0);
  protected readonly pointValueMinor = computed(() => this.customerLoyalty()?.pointValueMinor ?? this.loyaltySettings()?.pointValueMinor ?? 0);
  protected readonly redeemValue = computed(() => redeemValueMinor(this.redeemPoints(), this.pointValueMinor()));
  protected readonly eligibleMinor = computed(() => loyaltyEligibleMinor(this.lines()));
  protected readonly giftLinesMinor = computed(() => giftCardLinesMinor(this.lines()));
  /** Balance of the ticket's customer (fresh from the API, else as attached). */
  protected readonly pointsBalance = computed(() => this.customerLoyalty()?.points ?? this.customer()?.loyaltyPoints ?? 0);
  protected readonly maxRedeem = computed(() => maxRedeemPoints(this.pointsBalance(), this.pointValueMinor(), this.eligibleMinor()));
  /** What is wrong with the points redeemed on this ticket (after the cart changed), or null. */
  protected readonly redeemProblem = computed(() =>
    this.redeemPoints() > 0
      ? redeemError(this.redeemPoints(), {
          rules: this.customerLoyalty() ?? this.loyaltySettings(),
          hasCustomer: !!this.customer(),
          balance: this.pointsBalance(),
          eligibleMinor: this.eligibleMinor(),
        })
      : null,
  );
  /** What the customer pays: the cart total less the points redeemed. */
  protected readonly amountDue = computed(() => Math.max(0, this.totals().totalMinor - this.redeemValue()));
  protected readonly earnPreview = computed(() => pointsEarned(Math.max(0, this.eligibleMinor() - this.redeemValue()), this.loyaltySettings()?.pointsPerEuro ?? 0));
  protected redeemInput = '';
  protected readonly redeemInputError = signal<string | null>(null);
  protected readonly giftCardSaleOpen = signal(false);
  protected readonly giftCardPayOpen = signal(false);
  protected readonly maskedGiftCard = maskedGiftCard;
  /** Gift cards the last sale issued, with their codes (only from the creating response, kept in memory). */
  protected readonly issuedCards = computed<IssuedGiftCard[]>(() => (this.lastReceipt()?.issuedGiftCards ?? []).filter((c) => !!c.code));

  protected readonly settlement = computed(() => settlePayments(this.amountDue(), this.payments(), this.register.value()?.cashRounding ?? false, this.giftLinesMinor()));
  /** Next banknotes for the amount the cash line has to cover. */
  protected readonly cashQuick = computed(() => {
    const cashLine = this.payments().find((p) => p.method === 'Cash');
    return cashSuggestions(remainingFor(this.amountDue(), this.payments(), cashLine?.id ?? null), 5);
  });
  /** What may still be paid by gift card: the rest, without the gift cards sold on this ticket. */
  protected readonly giftCardPayable = computed(() => Math.max(0, Math.min(remainingFor(this.amountDue(), this.payments()), this.amountDue() - this.giftLinesMinor() - this.settlement().giftCardMinor)));
  protected readonly usedGiftCodes = computed(() => this.payments().filter((p) => p.giftCard).map((p) => p.giftCard!.code));
  protected readonly checkoutError = signal<string | null>(null);
  protected readonly paying = signal(false);
  protected readonly methods: PosPaymentMethod[] = ['Cash', 'Card', 'GiftCard'];

  // ---- offline mode
  /** The API is unreachable right now. */
  protected readonly offline = computed(() => !this.reach.online());
  protected readonly offlineMode = computed(() => !!this.register.value()?.offlineMode);
  /** The sale just recorded on this device (provisional receipt on the receipt screen). */
  protected readonly lastOffline = signal<QueueEntry | null>(null);
  /** When the cached products and prices of this register were last refreshed (shown while offline). */
  protected readonly cacheDate = signal<string | null>(null);

  // ---- card terminal
  /** Card payment the terminal approved for this ticket and that no receipt uses yet. */
  protected readonly attachedCard = computed(() => this.order().card ?? null);
  protected readonly cardLabel = cardLabel;
  /** The terminal's answer was lost: the cashier must check on the terminal (dialog). */
  protected readonly cardUncertain = signal<{ text: string; amountMinor: number } | null>(null);
  protected readonly reversing = signal(false);

  // ---- after the sale
  protected readonly lastReceipt = signal<Receipt | null>(null);
  /** Public e-receipt link of the last receipt, once created (QR on the customer display). */
  protected readonly receiptUrl = signal<string | null>(null);
  protected readonly confirmDiscard = signal(false);

  // ---- dialogs: line note, customer, price (pos.manage)
  protected readonly noteOpen = signal(false);
  protected noteText = '';
  protected readonly customerOpen = signal(false);
  protected readonly priceTarget = signal<PriceTarget | null>(null);
  private readonly dialogOpen = computed(() => this.confirmDiscard() || this.noteOpen() || this.customerOpen() || !!this.priceTarget() || !!this.cardUncertain() || !!this.terminals.active() || this.giftCardSaleOpen() || this.giftCardPayOpen());

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
    // Customer display: publish what the customer may see (never the customer's data, notes or the cashier).
    effect(() => {
      const id = this.registerId();
      const texts = this.branding.receiptBranding();
      const state = displayStateFromTill({
        sessionOpen: !!this.session(),
        screen: this.screen(),
        lines: this.lines(),
        settlement: this.screen() === 'payment' ? this.settlement() : null,
        loyalty: this.redeemPoints() ? { points: this.redeemPoints(), valueMinor: this.redeemValue() } : null,
        receipt: this.lastReceipt() ?? this.offlineDone(),
        receiptUrl: this.receiptUrl(),
        idleMessage: messageFromReceiptText(texts?.receiptHeader),
        doneMessage: messageFromReceiptText(texts?.receiptFooter),
        locale: this.i18n.lang(),
      });
      untracked(() => this.display.publish(id, state));
    });
    // Leaving the selling screen (in-app navigation, reload or closing the tab): the display shows the welcome screen.
    const displayIdle = () =>
      this.display.publish(this.registerId(), idleDisplayState(this.i18n.lang(), messageFromReceiptText(this.branding.receiptBranding()?.receiptHeader)));
    if (typeof window !== 'undefined') window.addEventListener('pagehide', displayIdle);
    inject(DestroyRef).onDestroy(() => {
      if (typeof window !== 'undefined') window.removeEventListener('pagehide', displayIdle);
      displayIdle();
      this.display.release(this.registerId());
    });
    // Registers in offline mode keep a copy of their products, prices and customers on this device.
    effect(() => {
      const reg = this.register.value();
      const online = this.reach.online();
      if (reg?.offlineMode && online && this.session()) untracked(() => void this.offlineData.refresh(reg));
    });
    effect(() => {
      const id = this.registerId();
      if (this.offline()) untracked(() => void this.offlineData.cachedAt(id).then((at) => this.cacheDate.set(at)));
    });
    // Back online: the register, its session and the stock may have changed; queued sales were booked meanwhile.
    let wasOffline = false;
    effect(() => {
      const offline = this.offline();
      const booked = this.queue.synced().length;
      if ((wasOffline && !offline) || booked) {
        untracked(() => {
          this.register.reload();
          this.sessionQuery.reload();
          this.grid()?.reload();
        });
      }
      wasOffline = offline;
    });
    // Loyalty settings of the organization (read once; a failure only means no point redemption at this till).
    effect(() => {
      if (this.session() && this.reach.online() && !untracked(() => this.loyaltySettings()))
        untracked(() => firstValueFrom(this.loyaltyApi.settings()).then((s) => this.loyaltySettings.set(s), () => undefined));
    });
    // The ticket's customer changed (switching tickets, attach, detach): load their current points.
    effect(() => {
      const id = this.customer()?.id ?? null;
      const enabled = this.loyaltyOn();
      untracked(() => {
        this.customerLoyalty.set(null);
        if (id && enabled) void this.loadCustomerLoyalty(id);
      });
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
    if (this.order().card) return; // an approved card payment must be reversed first (the dialog says so)
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
      this.addLookup(await firstValueFrom(this.offlineData.lookup(code, this.registerId(), this.customer()?.id ?? null)));
    } catch (e) {
      const reason = e instanceof OfflineLookupError ? this.i18n.t('pos.offline.lookup.' + e.reason) : posErrorText(this.i18n, e);
      this.scanError.set(this.i18n.t('pos.till.scanFailed', { code }) + ' ' + reason);
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
    // A pricelist price: remember the regular price and the list's name for the line.
    const lines =
      found.priceSource === 'Pricelist'
        ? result.lines.map((l) => (l.key === result.key ? { ...l, basePriceMinor: found.basePriceMinor ?? null, pricelistName: this.customer()?.pricelistName ?? null } : l))
        : result.lines;
    this.setLines(lines);
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
    // A gift card line has a fixed amount: the numpad can only remove it.
    if (line.kind === 'GiftCard') {
      if (key === 'backspace' || key === 'clear') this.removeLine(line.key);
      return;
    }
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
    if (line.kind === 'GiftCard') return false;
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
    const otherCustomer = order.customer?.id !== c.id;
    const otherList = otherCustomer || (order.customer?.pricelistName ?? null) !== (c.pricelistName ?? null);
    this.orders.update((s) =>
      updateOrder(s, order.id, {
        customer: { id: c.id, name: c.name, company: c.company, loyaltyPoints: c.loyaltyPoints ?? 0, pricelistName: c.pricelistName ?? null },
        redeemPoints: otherCustomer ? null : order.redeemPoints,
      }),
    );
    this.customerOpen.set(false);
    this.checkoutError.set(null);
    if (otherList) void this.repriceFor(c.id);
    this.focusCode();
  }

  protected detachCustomer(): void {
    const order = this.order();
    this.orders.update((s) => updateOrder(s, order.id, { customer: null, redeemPoints: null }));
    this.customerOpen.set(false);
    void this.repriceFor(null);
    this.focusCode();
  }

  /**
   * The customer changed: lines that still carry the till price follow the customer's pricelist (or the regular price
   * again); prices the cashier typed stay. Without a connection the lines keep their prices (the API checks at the sale).
   */
  private async repriceFor(customerId: string | null): Promise<void> {
    const orderId = this.order().id;
    const targets = repriceTargets(this.lines());
    if (!targets.length || this.offline()) return;
    const prices = await firstValueFrom(
      forkJoin(
        targets.map((pid) =>
          this.api.price(pid, this.registerId(), customerId).pipe(
            map((p): [string, ResolvedPrice] => [pid, { priceMinor: p.priceMinor, priceSource: p.priceSource, basePriceMinor: p.basePriceMinor ?? null, pricelistName: p.pricelistName ?? null }]),
            catchError(() => of(null)),
          ),
        ),
      ),
    );
    const found = new Map(prices.filter((x): x is [string, ResolvedPrice] => !!x));
    const order = this.orders().orders.find((o) => o.id === orderId);
    if (!order || !found.size) return;
    const r = repriceLines(order.lines, found);
    this.orders.update((s) => updateOrder(s, orderId, { lines: r.lines }));
    if (r.changed) this.toast.success(this.i18n.t('pos.pricelists.repriced', { count: r.changed }));
  }

  private async loadCustomerLoyalty(customerId: string): Promise<void> {
    try {
      const l = await firstValueFrom(this.loyaltyApi.customer(customerId, { pageSize: 1 }));
      if (this.customer()?.id === customerId) this.customerLoyalty.set(l);
    } catch {
      /* the points on the attached customer are shown instead; the API checks the balance at the sale */
    }
  }

  // =============================================================== gift cards and points
  protected sellGiftCard(amountMinor: number): void {
    this.giftCardSaleOpen.set(false);
    if (this.screen() === 'receipt') this.startNextOrder();
    const line = giftCardLine('g:' + newIdempotencyKey(), amountMinor, this.i18n.t('pos.giftCards.lineDescription'));
    this.setLines([...this.lines(), line]);
    this.selectedKey.set(line.key);
    this.focusCode();
  }

  protected addGiftCardPayment(c: GiftCardPaymentChoice): void {
    this.giftCardPayOpen.set(false);
    const id = newIdempotencyKey();
    this.setPayments([...this.payments(), { id, method: 'GiftCard', input: minorToPlain(c.amountMinor), giftCard: { code: c.code, last4: c.last4, balanceMinor: c.balanceMinor } }]);
    this.selectedPaymentId.set(id);
    this.payFresh = true;
  }

  protected chooseMethod(m: PosPaymentMethod): void {
    if (m === 'GiftCard') this.giftCardPayOpen.set(true);
    else this.addPayment(m);
  }

  protected applyRedeem(points: number | null = parsePoints(this.redeemInput)): void {
    const problem = redeemError(points, {
      rules: this.customerLoyalty() ?? this.loyaltySettings(),
      hasCustomer: !!this.customer(),
      balance: this.pointsBalance(),
      eligibleMinor: this.eligibleMinor(),
    });
    if (problem) {
      this.redeemInputError.set(this.redeemErrorText(problem));
      return;
    }
    this.redeemInputError.set(null);
    const order = this.order();
    this.orders.update((s) => updateOrder(s, order.id, { redeemPoints: points }));
    this.redeemInput = '';
  }

  protected redeemValueOf(points: number): number {
    return redeemValueMinor(points, this.pointValueMinor());
  }

  protected removeRedeem(): void {
    const order = this.order();
    this.orders.update((s) => updateOrder(s, order.id, { redeemPoints: null }));
  }

  protected redeemErrorText(problem: string): string {
    const rules = this.customerLoyalty() ?? this.loyaltySettings();
    return this.i18n.t('pos.loyalty.redeem.errors.' + problem, {
      min: rules?.minRedeemPoints ?? 0,
      balance: this.pointsBalance(),
      max: this.maxRedeem(),
    });
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
    this.setPayments(addPaymentLine(this.payments(), method, this.amountDue(), id));
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
    const rest = remainingFor(this.amountDue(), this.payments(), cash?.id ?? null);
    if (rest > 0) this.setCash(rest);
  }

  /** F4 / "Karte (Rest)": card for what the other payments leave open (never more than the total). */
  protected cardRest(): void {
    if (this.screen() !== 'payment' && !this.openPayment()) return;
    const card = this.payments().find((p) => p.method === 'Card');
    const rest = remainingFor(this.amountDue(), this.payments(), card?.id ?? null);
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
    const redeem = this.redeemProblem();
    if (redeem) {
      this.checkoutError.set(this.redeemErrorText(redeem));
      return;
    }
    this.paying.set(true);
    this.checkoutError.set(null);
    try {
      // No connection to the API: record the sale on this device (registers in offline mode).
      if (this.offline()) return await this.sellOffline(order.id, s);
      // Card amount: run the terminal first (or reuse the payment it already approved for this ticket).
      const payments = await this.settleCard(order.id, s.payments);
      if (!payments) return;
      await this.sendSale(order.id, payments, s);
    } finally {
      this.paying.set(false);
    }
  }

  /**
   * Records the sale in the offline queue of this device (IndexedDB) instead of sending it. The customer gets a
   * provisional receipt; receipt number and TSE signature exist only once the queue has synced. Card amounts are
   * confirmed by the cashier (the terminal runs on its own), unless a terminal payment was already approved online.
   */
  private async sellOffline(orderId: string, s: PaymentLinesSettlementLike): Promise<void> {
    const order = this.order();
    const reg = this.register.value();
    const block = offlineBlock({
      offlineMode: this.offlineMode(),
      sessionOpen: !!this.session(),
      persistent: this.queue.persistent(),
      methods: s.payments.map((p) => p.method),
      cardUnreported: !!order.card && !order.card.reported,
      sellsGiftCard: order.lines.some((l) => l.kind === 'GiftCard'),
      redeemsPoints: !!order.redeemPoints,
    });
    if (block || !reg) {
      this.checkoutError.set(this.i18n.t('pos.offline.blocked.' + (block ?? 'noSession')));
      return;
    }
    const card = order.card ?? null;
    if (card && card.amountMinor !== cardAmount(s.payments)) {
      this.checkoutError.set(this.i18n.t('pos.terminals.flow.amountChanged', { amount: formatMoney(card.amountMinor), card: cardLabel(card) }));
      return;
    }
    const payments = card ? withCardTransaction(s.payments, card.transactionId) : s.payments;
    const body = buildSaleRequest(this.registerId(), order.lines, payments, order.customer?.id ?? null, order.redeemPoints ?? null);
    const online = JSON.stringify(body);
    // A request of exactly this sale may already have reached the API (its answer was lost): keep its key, so the
    // sync cannot book the sale twice.
    const sentOnline = order.pending && order.pending.body === online ? order.pending : null;
    try {
      const entry = await this.queue.enqueue({
        id: sentOnline?.key ?? newIdempotencyKey(),
        organizationId: this.store.organizationId() ?? '',
        registerId: this.registerId(),
        registerName: reg.name,
        recordedAt: new Date().toISOString(),
        body,
        onlineBody: sentOnline ? online : null,
        lines: order.lines,
        payments,
        tenderedCashMinor: s.cashMinor,
        changeMinor: s.changeMinor,
        roundingMinor: s.roundingMinor ?? 0,
        cashierName: this.cashiers.actingName() ?? null,
      });
      void this.offlineData.sold(this.registerId(), body.lines).then(() => this.grid()?.reload());
      this.lastPrices.remember(Object.fromEntries(body.lines.filter((l) => l.productId).map((l) => [l.productId!, l.unitPriceMinor])));
      this.receiptUrl.set(null);
      this.lastReceipt.set(null);
      this.lastOffline.set(entry);
      this.finishOrder(orderId);
      this.screen.set('receipt');
      this.focusId('pos-new-sale');
    } catch {
      // Nothing was stored: the ticket stays as it is.
      this.checkoutError.set(this.i18n.t('pos.offline.storeFailed'));
    }
  }

  /** The provisional receipt as the customer display's "done" screen. */
  private offlineDone() {
    const e = this.lastOffline();
    return e ? { totalGrossMinor: e.totalMinor, changeMinor: e.changeMinor, roundingMinor: e.roundingMinor, lines: e.lines, payments: e.payments } : null;
  }

  /**
   * Card part of a checkout. Returns the payments to send (with the card transaction when a terminal approved it) or
   * null when the checkout stops here — the reason is then shown in the payment dialog.
   */
  private async settleCard(orderId: string, payments: SalePayment[]): Promise<SalePayment[] | null> {
    const cardMinor = cardAmount(payments);
    let card = this.order().card ?? null;
    const terminal = cardMinor > 0 && !card ? await this.terminals.effective(this.registerId()) : null;
    const step = cardStep(card, cardMinor, terminal, !!this.order().cardManual);
    if (step === 'none' || step === 'manual') return payments;
    if (step === 'mismatch' || step === 'orphan') {
      this.checkoutError.set(this.i18n.t('pos.terminals.flow.amountChanged', { amount: formatMoney(card!.amountMinor), card: cardLabel(card!) }));
      return null;
    }
    if (step === 'charge') {
      const run = await this.terminals.charge(this.registerId(), 'Payment', cardMinor);
      if (run.kind === 'manual') return payments;
      if (run.kind === 'uncertain') {
        this.cardUncertain.set({ text: run.text, amountMinor: cardMinor });
        return null;
      }
      if (run.kind !== 'approved') {
        this.checkoutError.set(run.text);
        return null;
      }
      card = run.card;
      // Stored with the ticket right away: a reload or a failed sale must not lose an approved charge.
      this.orders.update((st) => updateOrder(st, orderId, { card }));
    }
    if (card && !card.reported && card.pendingResult) {
      if (!(await this.terminals.report(card.transactionId, card.pendingResult))) {
        this.checkoutError.set(this.i18n.t('pos.terminals.flow.unreported', { amount: formatMoney(card.amountMinor) }));
        return null;
      }
      card = { ...card, reported: true, pendingResult: null };
      this.orders.update((st) => updateOrder(st, orderId, { card }));
    }
    return withCardTransaction(payments, card!.transactionId);
  }

  /** The terminal's answer was lost and the cashier checked the terminal: the payment went through → confirm by hand. */
  protected confirmCardManually(): void {
    const order = this.order();
    this.cardUncertain.set(null);
    this.orders.update((st) => updateOrder(st, order.id, { cardManual: true }));
    void this.validate();
  }

  /** Reverses the card payment attached to this ticket on the terminal (the amount changed or the sale is given up). */
  protected async reverseAttachedCard(): Promise<void> {
    const order = this.order();
    const card = order.card;
    if (!card || this.reversing() || this.paying()) return;
    this.reversing.set(true);
    this.checkoutError.set(null);
    try {
      const r = await this.terminals.reverse(this.registerId(), card);
      if (r.kind === 'reversed') {
        this.orders.update((st) => updateOrder(st, order.id, { card: null }));
        this.toast.success(this.i18n.t('pos.terminals.flow.reversed', { amount: formatMoney(card.amountMinor) }));
      } else if (r.kind === 'reversedUnreported') {
        this.orders.update((st) => updateOrder(st, order.id, { card: null }));
        this.checkoutError.set(r.text);
      } else if (r.kind === 'notReversible') {
        this.orders.update((st) => updateOrder(st, order.id, { card: null }));
        this.checkoutError.set(this.i18n.t('pos.terminals.flow.notReversible'));
      } else {
        this.checkoutError.set(this.i18n.t('pos.terminals.flow.reversalFailed', { amount: formatMoney(card.amountMinor), reason: r.text }));
      }
    } finally {
      this.reversing.set(false);
    }
  }

  /** The API refused the sale although the terminal had approved the card: reverse it and say plainly what happened. */
  private async cardAfterSaleFailure(orderId: string, status: number, reason: string): Promise<string | null> {
    const card = this.order().card;
    if (!card) return null;
    const amount = formatMoney(card.amountMinor);
    if (afterSaleFailure(status) === 'keep') return `${reason} ${this.i18n.t('pos.terminals.flow.cardKept', { amount })}`;
    const r = await this.terminals.reverse(this.registerId(), card);
    switch (r.kind) {
      case 'reversed':
        this.orders.update((st) => updateOrder(st, orderId, { card: null }));
        return this.i18n.t('pos.terminals.flow.saleFailedReversed', { reason, amount });
      case 'reversedUnreported':
        this.orders.update((st) => updateOrder(st, orderId, { card: null }));
        return `${this.i18n.t('pos.terminals.flow.saleFailedReversed', { reason, amount })} ${r.text}`;
      case 'notReversible':
        this.orders.update((st) => updateOrder(st, orderId, { card: null }));
        return `${reason} ${this.i18n.t('pos.terminals.flow.notReversible')}`;
      case 'failed':
        return this.i18n.t('pos.terminals.flow.saleFailedNotReversed', { reason, amount, detail: r.text });
    }
  }

  private async sendSale(orderId: string, payments: SalePayment[], s: PaymentLinesSettlementLike): Promise<void> {
    const order = this.order();
    const body = buildSaleRequest(this.registerId(), order.lines, payments, order.customer?.id ?? null, order.redeemPoints ?? null);
    const attempt = attemptFor(order.pending, JSON.stringify(body), newIdempotencyKey);
    this.orders.update((st) => updateOrder(st, order.id, { pending: attempt }));
    try {
      const receipt = await firstValueFrom(this.api.sell(body, attempt.key));
      this.lastPrices.remember(Object.fromEntries(body.lines.filter((l) => l.productId).map((l) => [l.productId!, l.unitPriceMinor])));
      this.receiptUrl.set(null);
      this.lastOffline.set(null);
      this.lastReceipt.set(receipt);
      void this.offlineData.sold(this.registerId(), body.lines); // keeps the offline copy of the stock roughly right
      void this.fiscalStatus?.refresh(); // a signed receipt ends a TSE outage; a failed one starts it
      this.finishOrder(order.id);
      this.screen.set('receipt');
      // Auto-print / cash drawer per the device's printer profile (ADR-0018), once the receipt is on the page.
      afterNextRender(() => void this.printer.afterSale(receipt), { injector: this.injector });
      this.sessionQuery.reload();
      this.grid()?.reload();
      this.focusId('pos-new-sale');
    } catch (e) {
      const keep = keepAttemptAfter(problemStatus(e));
      if (!keep) this.orders.update((st) => updateOrder(st, order.id, { pending: null }));
      // The API did not answer: if the till is offline now and may sell offline, the sale goes into the queue with
      // the key of this attempt (should the request have arrived after all, the sync replays it instead of selling twice).
      if (isUnreachable(problemStatus(e)) && this.offlineMode() && !(await this.reach.check())) return this.sellOffline(orderId, s);
      const problem = toApiProblem(e);
      const reason = posErrorText(this.i18n, e) + (keep ? ' ' + this.i18n.t('pos.till.retryHint') : '');
      this.checkoutError.set(reason);
      const withCard = await this.cardAfterSaleFailure(orderId, problemStatus(e), reason);
      if (withCard) this.checkoutError.set(withCard);
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
    }
  }

  /** The sold ticket is removed; selling continues on a fresh ticket (parked ones stay parked). */
  private finishOrder(id: string): void {
    this.customerLoyalty.set(null);
    this.redeemInput = '';
    this.redeemInputError.set(null);
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
    this.lastOffline.set(null);
    this.pane.set('products');
    this.screen.set('products');
    this.focusCode();
  }

  /** Prints with the device's printer profile; without one (or after a printer error) the browser print dialog opens. */
  protected print(): void {
    const receipt = this.lastReceipt();
    if (receipt) return void this.printer.printReceipt(receipt);
    const entry = this.lastOffline();
    if (entry) void this.printProvisional(entry);
  }

  /** Provisional receipt of an offline sale: thermal printer profile when the device knows one, else the print dialog. */
  private printProvisional(entry: QueueEntry): Promise<unknown> {
    const p = this.branding.profile();
    const sellerLines = p ? [...receiptLines(p.receiptHeader), p.name, p.street, `${p.postalCode} ${p.city}`.trim()].filter(Boolean) : [];
    return this.printer.printOther(entry.registerId, (profile) => provisionalLayout(entry, { width: profile.charsPerLine, codepage: profile.codepage, t: germanT, locale: 'de-DE', sellerLines }));
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
    if (this.pane() === 'cart' && narrowScreen()) return this.closeCart();
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

  /**
   * Focuses the scan/search field. Touch-only devices skip it (it would pop up the on-screen keyboard after every
   * action); a barcode scanner still works there because printable keys are routed to the field (onKeydown).
   */
  protected focusCode(): void {
    if (touchOnly()) return;
    afterNextRender(() => this.codeInput()?.nativeElement.focus(), { injector: this.injector });
  }

  // ---- phones: the cart is a full-height sheet over the products (opened from the bottom bar)
  protected openCart(): void {
    this.pane.set('cart');
    this.focusId('pos-cart-heading');
  }

  protected closeCart(): void {
    this.pane.set('products');
    afterNextRender(() => this.cartOpener()?.nativeElement.focus(), { injector: this.injector });
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

/** Below the md breakpoint (phones): one pane, the cart is a sheet. */
function narrowScreen(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia?.('(max-width: 767.98px)').matches;
}

/** A device used by touch only (no hover, coarse pointer): phones and tablets without keyboard/mouse. */
function touchOnly(): boolean {
  return typeof window !== 'undefined' && !!window.matchMedia?.('(hover: none) and (pointer: coarse)').matches;
}

function isInsideDialog(target: EventTarget | null): boolean {
  return !!(target as HTMLElement | null)?.closest?.('dialog');
}
