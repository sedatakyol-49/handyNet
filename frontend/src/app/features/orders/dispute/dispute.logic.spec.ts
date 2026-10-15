import { DISPUTE_PARTIES, DISPUTE_REASONS, DISPUTE_STATUSES, Dispute, DisputeStatus, ORDER_STATUSES } from '../../../core/api/models';
import { flatten } from '../../../core/i18n/i18n.service';
import de from '../../../core/i18n/locales/de.json';
import en from '../../../core/i18n/locales/en.json';
import {
  DISPUTE_TEXT_MAX,
  NO_DISPUTE_ACTIONS,
  canReportProblem,
  chronological,
  currentDispute,
  disputeActions,
  isDisputeActive,
  ownParty,
  reasonKey,
  shortId,
  validateDecision,
  validateDisputeText,
  validateReport,
} from './dispute.logic';

const NOW = Date.parse('2026-10-15T12:00:00Z');
const later = new Date(NOW + 3_600_000).toISOString();
const earlier = new Date(NOW - 1000).toISOString();

const dispute = (status: DisputeStatus, over: Partial<Dispute> = {}): Dispute => ({
  id: 'd-' + status,
  orderId: 'o1',
  role: 'buyer',
  buyerOrganizationId: 'b',
  sellerOrganizationId: 's',
  reason: 'Damaged',
  status,
  openedAt: '2026-10-15T10:00:00Z',
  escalatedAt: null,
  resolvedAt: null,
  resolvedBy: null,
  resolution: null,
  messages: [],
  ...over,
});

const all = { canRead: true, canPlace: true, canFulfil: true };

describe('isDisputeActive / currentDispute', () => {
  it('treats Open and Escalated as active', () => {
    expect(isDisputeActive('Open')).toBe(true);
    expect(isDisputeActive('Escalated')).toBe(true);
    expect(isDisputeActive('Refunded')).toBe(false);
    expect(isDisputeActive('ClosedWithoutRefund')).toBe(false);
  });

  it('prefers the active dispute, otherwise the newest', () => {
    const closed = dispute('ClosedWithoutRefund');
    const open = dispute('Open');
    expect(currentDispute([closed, open])).toBe(open);
    expect(currentDispute([closed, dispute('Refunded')])).toBe(closed);
    expect(currentDispute([])).toBeNull();
  });
});

describe('canReportProblem', () => {
  const delivered = { status: 'Delivered' as const, inspectionEndsAt: later };

  it('allows the buyer to report while Delivered and the inspection window runs', () => {
    expect(canReportProblem(delivered, [], true, true, NOW)).toBe(true);
  });

  it('is not offered to the seller or without orders.place', () => {
    expect(canReportProblem(delivered, [], false, true, NOW)).toBe(false);
    expect(canReportProblem(delivered, [], true, false, NOW)).toBe(false);
  });

  it('is not offered after the inspection window or in other states', () => {
    expect(canReportProblem({ status: 'Delivered', inspectionEndsAt: earlier }, [], true, true, NOW)).toBe(false);
    expect(canReportProblem({ status: 'Delivered', inspectionEndsAt: null }, [], true, true, NOW)).toBe(false);
    for (const status of ORDER_STATUSES.filter((s) => s !== 'Delivered')) {
      expect(canReportProblem({ status, inspectionEndsAt: later }, [], true, true, NOW)).toBe(false);
    }
  });

  it('is not offered while a dispute is active', () => {
    expect(canReportProblem(delivered, [dispute('Open')], true, true, NOW)).toBe(false);
    expect(canReportProblem(delivered, [dispute('Escalated')], true, true, NOW)).toBe(false);
  });
});

describe('disputeActions', () => {
  it('seller: message, escalate and refund while Open; no withdraw', () => {
    expect(disputeActions(dispute('Open', { role: 'seller' }), all)).toEqual({ message: true, escalate: true, refund: true, withdraw: false });
  });

  it('buyer: message, escalate and withdraw while Open; no refund', () => {
    expect(disputeActions(dispute('Open', { role: 'buyer' }), all)).toEqual({ message: true, escalate: true, refund: false, withdraw: true });
  });

  it('no escalation once Escalated, but messages and resolving remain possible', () => {
    expect(disputeActions(dispute('Escalated', { role: 'seller' }), all)).toEqual({ message: true, escalate: false, refund: true, withdraw: false });
    expect(disputeActions(dispute('Escalated', { role: 'buyer' }), all)).toEqual({ message: true, escalate: false, refund: false, withdraw: true });
  });

  it('nothing for closed disputes', () => {
    for (const status of ['Refunded', 'ClosedWithoutRefund'] as const) {
      expect(disputeActions(dispute(status, { role: 'seller' }), all)).toEqual(NO_DISPUTE_ACTIONS);
      expect(disputeActions(dispute(status, { role: 'buyer' }), all)).toEqual(NO_DISPUTE_ACTIONS);
    }
    expect(disputeActions(null, all)).toEqual(NO_DISPUTE_ACTIONS);
  });

  it('respects permissions', () => {
    expect(disputeActions(dispute('Open', { role: 'seller' }), { ...all, canFulfil: false }).refund).toBe(false);
    expect(disputeActions(dispute('Open', { role: 'buyer' }), { ...all, canPlace: false }).withdraw).toBe(false);
    expect(disputeActions(dispute('Open', { role: 'buyer' }), { ...all, canRead: false })).toEqual(NO_DISPUTE_ACTIONS);
  });
});

describe('thread helpers', () => {
  it('maps the role to the own party', () => {
    expect(ownParty('buyer')).toBe('Buyer');
    expect(ownParty('seller')).toBe('Seller');
  });

  it('sorts messages chronologically and keeps the order of equal timestamps', () => {
    const a = { from: 'Buyer', text: 'a', sentAt: '2026-10-15T10:00:00Z' };
    const b = { from: 'Seller', text: 'b', sentAt: '2026-10-15T09:00:00Z' };
    const c = { from: 'Platform', text: 'c', sentAt: '2026-10-15T10:00:00Z' };
    expect(chronological([a, b, c]).map((m) => m.text)).toEqual(['b', 'a', 'c']);
  });

  it('shortens ids for the admin queue', () => {
    expect(shortId('0199f0c2-1234-7abc-8def-001122334455')).toBe('0199f0c2');
  });
});

describe('validation', () => {
  it('requires text and enforces the server maximum', () => {
    expect(validateDisputeText('')).toBe('disputes.validation.required');
    expect(validateDisputeText('   ')).toBe('disputes.validation.required');
    expect(validateDisputeText('ok')).toBeNull();
    expect(validateDisputeText('x'.repeat(DISPUTE_TEXT_MAX))).toBeNull();
    expect(validateDisputeText('x'.repeat(DISPUTE_TEXT_MAX + 1))).toBe('disputes.validation.tooLong');
  });

  it('report form needs a reason and a description', () => {
    expect(validateReport({ reason: '', description: '' })).toEqual({
      reason: 'disputes.validation.reasonRequired',
      description: 'disputes.validation.required',
    });
    expect(validateReport({ reason: 'Damaged', description: 'Display gebrochen' })).toEqual({});
  });

  it('admin decision needs an explicit choice and a resolution', () => {
    expect(validateDecision({ refund: null, resolution: '' })).toEqual({
      refund: 'disputes.validation.decisionRequired',
      resolution: 'disputes.validation.required',
    });
    // "No refund" (false) is a valid choice, not a missing one.
    expect(validateDecision({ refund: false, resolution: 'Ware unbeschädigt' })).toEqual({});
    expect(validateDecision({ refund: true, resolution: 'Erstattung' })).toEqual({});
  });
});

describe('translations', () => {
  type Dict = Parameters<typeof flatten>[0];
  const dict = { de: flatten(de as unknown as Dict), en: flatten(en as unknown as Dict) };

  it('has a label for every reason, status, party and the new order statuses in de and en', () => {
    for (const [lang, d] of Object.entries(dict)) {
      for (const r of DISPUTE_REASONS) expect(d[reasonKey(r)], `${lang} ${r}`).toBeTruthy();
      for (const s of DISPUTE_STATUSES) expect(d[`disputeStatus.${s}`], `${lang} ${s}`).toBeTruthy();
      for (const p of DISPUTE_PARTIES) expect(d[`disputeParty.${p}`], `${lang} ${p}`).toBeTruthy();
      for (const s of ORDER_STATUSES) expect(d[`orderStatus.${s}`], `${lang} ${s}`).toBeTruthy();
    }
    expect(dict.de['disputeParty.Buyer']).toBe('Käufer');
    expect(dict.de['disputeParty.Seller']).toBe('Verkäufer');
    expect(dict.de['disputeParty.Platform']).toBe('Plattform');
    expect(dict.de['orderStatus.Refunded']).toBe('Erstattet');
  });

  it('translates every dispute error code', () => {
    const codes = [
      'order.inspection_over',
      'order.invalid_transition',
      'order.not_found',
      'order.has_open_shipment',
      'dispute.exists',
      'dispute.closed',
      'dispute.not_found',
      'dispute.not_open',
      'dispute.not_escalated',
      'dispute.not_seller',
      'dispute.not_buyer',
      'dispute.seller_cannot_deny',
      'dispute.buyer_cannot_refund',
      'dispute.description',
      'dispute.message',
      'dispute.resolution',
    ];
    for (const d of Object.values(dict)) for (const c of codes) expect(d[`errors.codes.${c}`], c).toBeTruthy();
  });
});
