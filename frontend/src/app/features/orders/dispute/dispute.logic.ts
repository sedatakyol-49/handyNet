import { Dispute, DisputeParty, DisputeReason, DisputeStatus, Order } from '../../../core/api/models';
import { inspectionRunning } from '../shipping/shipping.logic';

/** Server limit for descriptions, messages and resolutions (`Dispute.MaxTextLength`). */
export const DISPUTE_TEXT_MAX = 4000;

/** Open and Escalated disputes still block the order (no completion, no payout). */
export function isDisputeActive(status: DisputeStatus): boolean {
  return status === 'Open' || status === 'Escalated';
}

/** The dispute the section focuses on: the active one, otherwise the newest (input is newest first). */
export function currentDispute(disputes: readonly Dispute[]): Dispute | null {
  return disputes.find((d) => isDisputeActive(d.status)) ?? disputes[0] ?? null;
}

/**
 * Buyer may report a problem: order Delivered, inspection window still running and no active dispute.
 * `canPlace` = B2BPurchasing + orders.place of the selected organization, which must be the order's buyer.
 * The API stays authoritative (`order.inspection_over`, `dispute.exists`).
 */
export function canReportProblem(
  order: Pick<Order, 'status' | 'inspectionEndsAt'>,
  disputes: readonly Dispute[],
  isBuyer: boolean,
  canPlace: boolean,
  now: number,
): boolean {
  if (!isBuyer || !canPlace) return false;
  if (disputes.some((d) => isDisputeActive(d.status))) return false;
  return inspectionRunning(order, now);
}

export interface DisputeActions {
  /** Write into the thread (either party while active). */
  message: boolean;
  /** Hand over to HandyNet (either party, only while Open). */
  escalate: boolean;
  /** Seller grants a full refund (orders.fulfil). */
  refund: boolean;
  /** Buyer withdraws the complaint (orders.place); the order completes and the seller is paid. */
  withdraw: boolean;
}

export const NO_DISPUTE_ACTIONS: DisputeActions = { message: false, escalate: false, refund: false, withdraw: false };

/**
 * Actions offered to the current organization, mirroring the API rules:
 * - messages (orders.read) while Open/Escalated, escalate only while Open;
 * - seller: refund (orders.fulfil) – the seller cannot close a dispute against the buyer;
 * - buyer: withdraw (orders.place) – the buyer cannot grant itself a refund.
 */
export function disputeActions(
  dispute: Pick<Dispute, 'role' | 'status'> | null,
  perms: { canRead: boolean; canPlace: boolean; canFulfil: boolean },
): DisputeActions {
  if (!dispute || !isDisputeActive(dispute.status) || !perms.canRead) return NO_DISPUTE_ACTIONS;
  const seller = dispute.role === 'seller';
  return {
    message: true,
    escalate: dispute.status === 'Open',
    refund: seller && perms.canFulfil,
    withdraw: !seller && perms.canPlace,
  };
}

/** Party of the calling organization in the thread. */
export function ownParty(role: Dispute['role']): DisputeParty {
  return role === 'seller' ? 'Seller' : 'Buyer';
}

/** Messages oldest first (the API already sorts; kept stable for equal timestamps). */
export function chronological<T extends { sentAt: string }>(messages: readonly T[]): T[] {
  return messages
    .map((m, i) => ({ m, i, t: Date.parse(m.sentAt) }))
    .sort((a, b) => a.t - b.t || a.i - b.i)
    .map((x) => x.m);
}

/** Translation key for a reason label (`disputeReason.*`). */
export function reasonKey(reason: DisputeReason | string): string {
  return `disputeReason.${reason}`;
}

/**
 * Validates a required free text (description, message, resolution). Returns a translation key or null.
 * Length is counted like the server (UTF-16 code units, before trimming).
 */
export function validateDisputeText(text: string, max = DISPUTE_TEXT_MAX): string | null {
  if (!text.trim()) return 'disputes.validation.required';
  if (text.length > max) return 'disputes.validation.tooLong';
  return null;
}

/** Report form: a reason must be chosen and the description is required. */
export function validateReport(form: { reason: string; description: string }): { reason?: string; description?: string } {
  const errors: { reason?: string; description?: string } = {};
  if (!form.reason) errors.reason = 'disputes.validation.reasonRequired';
  const description = validateDisputeText(form.description);
  if (description) errors.description = description;
  return errors;
}

/** Admin decision: refund yes/no must be chosen explicitly and the resolution is required. */
export function validateDecision(form: { refund: boolean | null; resolution: string }): { refund?: string; resolution?: string } {
  const errors: { refund?: string; resolution?: string } = {};
  if (form.refund === null) errors.refund = 'disputes.validation.decisionRequired';
  const resolution = validateDisputeText(form.resolution);
  if (resolution) errors.resolution = resolution;
  return errors;
}

/** Shortened GUID for tables without names (admin queue): first 8 hex digits. */
export function shortId(id: string): string {
  return id.slice(0, 8);
}
