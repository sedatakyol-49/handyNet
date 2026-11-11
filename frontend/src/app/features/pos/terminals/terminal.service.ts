import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { I18nService } from '../../../core/i18n/i18n.service';
import { StorageService } from '../../../core/storage.service';
import { posErrorText } from '../pos-error';
import { DEVICE_MANUAL, OrderCard, effectiveTerminal, orderCardFrom, outcomeMessageKey, reversalReceiptNumber, reversible } from './card-flow';
import { CardTransaction, CardTransactionKind, TerminalApi, TerminalProfile } from './terminal-api';
import { SimulatedChoice, TerminalOutcome, bridgeProgress, callBridge, resultRequest, simulatedOutcome } from './terminal-transports';

const deviceKey = (registerId: string) => `hn.pos.terminal.${registerId}`;
const PROGRESS_POLL_MS = 1500;
const REPORT_RETRY_MS = [0, 800, 2000];

/** What the terminal dialog shows while a run is in progress. */
export interface ActiveRun {
  profile: TerminalProfile;
  operation: 'payment' | 'refund' | 'reversal';
  amountMinor: number;
  /** choose = simulated terminal waits for the cashier's pick; waiting = real terminal; saving = result goes to the API. */
  phase: 'choose' | 'waiting' | 'saving';
  /** Last intermediate status of the terminal ("Bitte Karte einstecken"). */
  progress: string | null;
  abortRequested: boolean;
}

export type ChargeResult =
  /** No terminal connection for this device: the cashier confirms the card payment as before. */
  | { kind: 'manual' }
  | { kind: 'approved'; card: OrderCard; outcome: TerminalOutcome }
  /** Declined, aborted or failed without a booking: back to the payment dialog. */
  | { kind: 'rejected'; outcome: TerminalOutcome; text: string }
  /** Nobody knows whether the terminal booked it: the cashier must check on the terminal. */
  | { kind: 'uncertain'; outcome: TerminalOutcome; text: string }
  /** The API did not accept the attempt (unreachable, refused): the terminal was never started. */
  | { kind: 'error'; text: string };

export type ReverseResult =
  | { kind: 'reversed' }
  /** Reversed on the terminal, but the API could not be told: `text` says so; report again later. */
  | { kind: 'reversedUnreported'; text: string }
  /** The API says this payment must not be reversed (used by a receipt, or not approved). */
  | { kind: 'notReversible'; transaction: CardTransaction }
  | { kind: 'failed'; text: string };

/**
 * Card terminal runs of the till: picks the device's profile, records every attempt as a card transaction in the API
 * before the terminal starts, runs the transport and reports the result. Shows nothing itself — `active` drives the
 * terminal dialog in the POS shell.
 */
@Injectable({ providedIn: 'root' })
export class TerminalService {
  private readonly api = inject(TerminalApi);
  private readonly storage = inject(StorageService);
  private readonly i18n = inject(I18nService);
  private readonly cache = new Map<string, TerminalProfile[]>();
  private simulate: ((choice: SimulatedChoice) => void) | null = null;
  private sequence = Date.now() % 9000;

  readonly active = signal<ActiveRun | null>(null);

  /** Profiles of a register; when the API is unreachable the last loaded list (or none = manual) is used. */
  async profiles(registerId: string, reload = false): Promise<TerminalProfile[]> {
    const cached = this.cache.get(registerId);
    if (cached && !reload) return cached;
    try {
      const list = await firstValueFrom(this.api.list(registerId));
      this.cache.set(registerId, list);
      return list;
    } catch {
      return cached ?? [];
    }
  }

  setProfiles(registerId: string, list: TerminalProfile[]): void {
    this.cache.set(registerId, list);
  }

  deviceChoice(registerId: string): string {
    return this.storage.get(deviceKey(registerId)) ?? '';
  }

  setDeviceChoice(registerId: string, choice: string): void {
    this.storage.set(deviceKey(registerId), choice || null);
  }

  /** The terminal this device uses for card payments; null = manual confirmation. */
  async effective(registerId: string): Promise<TerminalProfile | null> {
    return effectiveTerminal(await this.profiles(registerId), this.deviceChoice(registerId));
  }

  /** Runs a card payment or refund on the device's terminal and records it (Pending → result). */
  async charge(registerId: string, kind: CardTransactionKind, amountMinor: number): Promise<ChargeResult> {
    const profile = await this.effective(registerId);
    if (!profile) return { kind: 'manual' };
    let transaction: CardTransaction;
    try {
      transaction = await firstValueFrom(this.api.createTransaction(registerId, { kind, amountMinor, terminalProfileId: profile.id }));
    } catch (e) {
      return { kind: 'error', text: this.i18n.t('pos.terminals.flow.notStarted', { reason: posErrorText(this.i18n, e) ?? '' }) };
    }
    const outcome = await this.runTerminal(profile, kind === 'Payment' ? 'payment' : 'refund', amountMinor);
    this.patch({ phase: 'saving' });
    const reported = await this.report(transaction.id, outcome);
    this.active.set(null);
    if (outcome.status === 'Approved') return { kind: 'approved', card: orderCardFrom(transaction.id, amountMinor, profile, outcome, reported), outcome };
    const text = this.outcomeText(outcome);
    return outcome.uncertain ? { kind: 'uncertain', outcome, text } : { kind: 'rejected', outcome, text };
  }

  /** Tells the API what the terminal answered; retried a few times because losing this would hide a card charge. */
  async report(transactionId: string, outcome: TerminalOutcome): Promise<boolean> {
    for (const delay of REPORT_RETRY_MS) {
      if (delay) await new Promise((resolve) => setTimeout(resolve, delay));
      try {
        await firstValueFrom(this.api.reportResult(transactionId, resultRequest(outcome)));
        return true;
      } catch (e) {
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        if (status !== 0 && status < 500) return false; // the API refused it: repeating does not help
      }
    }
    return false;
  }

  /**
   * Reverses an approved, unused card payment: asks the API first (a payment that a receipt uses must not be reversed
   * here), then the terminal, then records the reversal.
   */
  async reverse(registerId: string, card: Pick<OrderCard, 'transactionId' | 'profileId' | 'terminalReceiptNumber' | 'amountMinor'>): Promise<ReverseResult> {
    let transaction: CardTransaction;
    try {
      transaction = await firstValueFrom(this.api.transaction(card.transactionId));
    } catch (e) {
      return { kind: 'failed', text: this.i18n.t('pos.terminals.flow.reversalNotChecked', { reason: posErrorText(this.i18n, e) ?? '' }) };
    }
    if (!reversible(transaction)) return { kind: 'notReversible', transaction };
    const profiles = await this.profiles(registerId);
    const profile = profiles.find((p) => p.id === (card.profileId ?? transaction.terminalProfileId)) ?? null;
    if (!profile || profile.connection === 'Manual') return { kind: 'failed', text: this.i18n.t('pos.terminals.flow.reversalNoProfile') };
    const receiptNumber = reversalReceiptNumber(card.terminalReceiptNumber ?? transaction.terminalReceiptNumber);
    if (profile.connection === 'ZvtBridge' && receiptNumber === null) {
      return { kind: 'failed', text: this.i18n.t('pos.terminals.errors.noReceiptNumber') };
    }
    const outcome = await this.runTerminal(profile, 'reversal', card.amountMinor, receiptNumber);
    if (outcome.status !== 'Approved') {
      this.active.set(null);
      return { kind: 'failed', text: this.outcomeText(outcome) };
    }
    this.patch({ phase: 'saving' });
    const text = [outcome.test ? 'TEST' : null, outcome.resultText, outcome.traceNumber ? `Trace ${outcome.traceNumber}` : null].filter(Boolean).join(' · ') || null;
    const recorded = await this.recordReversal(card.transactionId, text);
    this.active.set(null);
    return recorded.ok ? { kind: 'reversed' } : { kind: 'reversedUnreported', text: this.i18n.t('pos.terminals.flow.reversalUnreported', { reason: recorded.text ?? '' }) };
  }

  /** Records a reversal in the API (after the terminal reversed, or after a manager reversed it by hand on the terminal). */
  async recordReversal(transactionId: string, resultText: string | null): Promise<{ ok: boolean; text: string | null }> {
    let last: unknown = null;
    for (const delay of REPORT_RETRY_MS) {
      if (delay) await new Promise((resolve) => setTimeout(resolve, delay));
      try {
        await firstValueFrom(this.api.reportReversal(transactionId, resultText?.slice(0, 300) ?? null));
        return { ok: true, text: null };
      } catch (e) {
        last = e;
        const status = e instanceof HttpErrorResponse ? e.status : 0;
        if (status !== 0 && status < 500) break;
      }
    }
    return { ok: false, text: posErrorText(this.i18n, last) };
  }

  /** Connection test from the profile page: the bridge registers at the terminal (no payment). */
  async test(profile: TerminalProfile): Promise<TerminalOutcome> {
    return callBridge(profile, 'status');
  }

  /** End-of-day batch (Kassenschnitt) on the terminal. */
  async endOfDay(profile: TerminalProfile): Promise<TerminalOutcome> {
    return callBridge(profile, 'end-of-day');
  }

  /** The cashier's pick in the simulated terminal dialog. */
  chooseSimulated(choice: SimulatedChoice): void {
    this.simulate?.(choice);
  }

  /** Asks the real terminal to abort the running transaction (works while it waits for a card). */
  async requestAbort(): Promise<void> {
    const run = this.active();
    if (!run || run.phase !== 'waiting' || run.profile.connection !== 'ZvtBridge') return;
    this.patch({ abortRequested: true });
    await callBridge(run.profile, 'abort');
  }

  /** Understandable text for an outcome that is not an approval. */
  outcomeText(outcome: TerminalOutcome): string {
    const text = this.i18n.t(outcomeMessageKey(outcome));
    const detail = outcome.resultText?.trim();
    return detail ? `${text} (${detail})` : text;
  }

  private patch(patch: Partial<ActiveRun>): void {
    this.active.update((run) => (run ? { ...run, ...patch } : run));
  }

  private async runTerminal(profile: TerminalProfile, operation: ActiveRun['operation'], amountMinor: number, receiptNumber: number | null = null): Promise<TerminalOutcome> {
    if (profile.connection === 'Simulated') {
      this.active.set({ profile, operation, amountMinor, phase: 'choose', progress: null, abortRequested: false });
      const choice = await new Promise<SimulatedChoice>((resolve) => (this.simulate = resolve));
      this.simulate = null;
      return simulatedOutcome(choice, operation === 'payment' ? 'Payment' : operation === 'refund' ? 'Refund' : 'Reversal', amountMinor, this.sequence++);
    }
    this.active.set({ profile, operation, amountMinor, phase: 'waiting', progress: null, abortRequested: false });
    // The bridge answers when the terminal is done; meanwhile ask it what the terminal is showing.
    let polling = false;
    const poll = setInterval(() => {
      if (polling) return;
      polling = true;
      void bridgeProgress(profile)
        .then((text) => {
          if (text && this.active()?.phase === 'waiting') this.patch({ progress: text });
        })
        .finally(() => (polling = false));
    }, PROGRESS_POLL_MS);
    try {
      return await callBridge(profile, operation, operation === 'reversal' ? { receiptNumber: receiptNumber ?? undefined } : { amountMinor });
    } finally {
      clearInterval(poll);
    }
  }
}

export { DEVICE_MANUAL };
