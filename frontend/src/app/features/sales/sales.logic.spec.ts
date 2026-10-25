import {
  addDays,
  computeTotals,
  customerLines,
  emptyLine,
  invoiceBadge,
  lineRequest,
  missingInvoiceAddress,
  parseAmount,
  parseLine,
  percentToDiscount,
  quotationTone,
  vatFromGross,
  vatOnNet,
} from './sales.logic';

describe('sales.logic VAT', () => {
  it('takes VAT out of gross amounts at 19 %, 7 % and 0 % (like the API)', () => {
    expect(vatFromGross(119_00, 1900)).toBe(19_00);
    expect(vatFromGross(107_00, 700)).toBe(7_00);
    expect(vatFromGross(10_00, 0)).toBe(0);
    expect(vatFromGross(-119_00, 1900)).toBe(-19_00);
    expect(vatFromGross(41_65, 1900)).toBe(6_65);
  });

  it('adds VAT to net amounts, half away from zero', () => {
    expect(vatOnNet(100_00, 1900)).toBe(19_00);
    expect(vatOnNet(1_05, 700)).toBe(7);
    expect(vatOnNet(50, 1900)).toBe(10);
    expect(vatOnNet(-50, 1900)).toBe(-10);
  });

  it('gross mode sums per rate and keeps §25a lines without VAT', () => {
    const t = computeTotals('Gross', [
      { amountMinor: 23_80, vatRateBasisPoints: 1900, margin25a: false },
      { amountMinor: 10_70, vatRateBasisPoints: 700, margin25a: false },
      { amountMinor: 250_00, vatRateBasisPoints: 1900, margin25a: true },
    ]);
    expect(t.vat.map((v) => [v.rateBasisPoints, v.netMinor, v.vatMinor])).toEqual([
      [1900, 20_00, 3_80],
      [700, 10_00, 70],
    ]);
    expect(t.marginGrossMinor).toBe(250_00);
    expect(t.totalGrossMinor).toBe(284_50);
    expect(t.totalNetMinor + t.totalVatMinor).toBe(t.totalGrossMinor);
  });

  it('net mode adds VAT per rate (same figures as the API unit test)', () => {
    const t = computeTotals('Net', [
      { amountMinor: 29_00, vatRateBasisPoints: 1900, margin25a: false },
      { amountMinor: 50_00, vatRateBasisPoints: 0, margin25a: false },
      { amountMinor: 100_00, vatRateBasisPoints: 1900, margin25a: true },
    ]);
    expect(t.vat[0]).toEqual({ rateBasisPoints: 1900, netMinor: 29_00, vatMinor: 5_51, grossMinor: 34_51 });
    expect(t.totalGrossMinor).toBe(34_51 + 50_00 + 100_00);
  });
});

describe('sales.logic lines', () => {
  it('refuses sub-cent and negative amounts instead of rounding', () => {
    expect(parseAmount('12,345')).toBeNull();
    expect(parseAmount('-1')).toBeNull();
    expect(parseAmount('12,50')).toBe(12_50);
    expect(parseAmount('', true)).toBe(0);
  });

  it('validates quantity, price, discount and description', () => {
    const ok = parseLine({ ...emptyLine(), description: 'Hülle', quantity: '3', unitPrice: '10,00', discount: '5' });
    expect(ok.value).toEqual({ quantity: 3, unitPriceMinor: 10_00, discountMinor: 5_00, amountMinor: 25_00 });
    expect(parseLine({ ...emptyLine(), description: '', quantity: '0', unitPrice: 'x' }).errors).toEqual({
      description: 'required', quantity: 'quantity', unitPrice: 'amount',
    });
    expect(parseLine({ ...emptyLine(), description: 'x', quantity: '1', unitPrice: '1,00', discount: '2,00' }).errors.discount).toBe('discountTooHigh');
    expect(parseLine({ ...emptyLine(), description: 'x', imei: '350000000000001', quantity: '2', unitPrice: '1' }).errors.quantity).toBe('deviceQuantity');
  });

  it('builds the API line; §25a lines always send 19 % (the API derives the scheme from the unit)', () => {
    const line = { ...emptyLine(), productId: 'p', imei: '350000000000001', margin25a: true, description: ' Phone ', unitPrice: '199', vatRateBasisPoints: 0 };
    expect(lineRequest(line, parseLine(line).value!)).toEqual({
      productId: 'p', imei: '350000000000001', description: 'Phone', note: null, quantity: 1, unitPriceMinor: 199_00, discountMinor: null, vatRateBasisPoints: 1900,
    });
  });

  it('turns a percent discount into truncated cents', () => {
    expect(percentToDiscount(9_99, 3, 10)).toBe(2_99);
    expect(percentToDiscount(10_00, 1, 0)).toBe(0);
  });
});

describe('sales.logic display', () => {
  it('shows one badge per invoice row', () => {
    const base = { status: 'Issued', kind: 'Invoice', cancelled: false, overdue: false, paymentState: 'Open' };
    expect(invoiceBadge({ ...base, status: 'Draft' }).key).toBe('Draft');
    expect(invoiceBadge({ ...base, kind: 'CreditNote' }).key).toBe('CreditNote');
    expect(invoiceBadge({ ...base, cancelled: true }).key).toBe('Cancelled');
    expect(invoiceBadge({ ...base, overdue: true })).toEqual({ key: 'Overdue', tone: 'danger' });
    expect(invoiceBadge({ ...base, paymentState: 'Paid' }).tone).toBe('success');
    expect(invoiceBadge({ ...base, paymentState: 'PartiallyPaid' }).key).toBe('PartiallyPaid');
    expect(quotationTone('Expired')).toBe('warning');
  });

  it('knows when an invoice address is incomplete and prints the customer block', () => {
    const c = { customerId: null, name: 'Max', company: 'Muster GmbH', street: 'Weg 1', postalCode: '20095', city: 'Hamburg', countryCode: 'AT', vatId: null };
    expect(missingInvoiceAddress(c)).toBe(false);
    expect(missingInvoiceAddress({ ...c, street: ' ' })).toBe(true);
    expect(missingInvoiceAddress(null)).toBe(true);
    expect(customerLines(c)).toEqual(['Muster GmbH', 'Max', 'Weg 1', '20095 Hamburg', 'AT']);
  });

  it('adds days across month ends', () => {
    expect(addDays('2026-10-22', 30)).toBe('2026-11-21');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
  });
});
