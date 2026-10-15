import { CarrierProfile, Shipment, TrackingEvent } from '../../../core/api/models';
import {
  ShipmentFormValue,
  eventsNewestFirst,
  formatRemaining,
  hasOpenShipment,
  inspectionRunning,
  insurable,
  insuranceRequired,
  parseCm,
  parseKgToGrams,
  profileFor,
  splitDuration,
  validateShipmentForm,
} from './shipping.logic';

/** Fake translator: renders "key{params}" so assertions can see which keys/params were used. */
const t = (key: string, params?: Record<string, string | number>) =>
  params ? `${key.split('.').pop()}:${Object.values(params).join(',')}` : key.split('.').pop()!;

describe('parseKgToGrams', () => {
  it('converts German and English decimals to integer grams', () => {
    expect(parseKgToGrams('1,25')).toBe(1250);
    expect(parseKgToGrams('1.25')).toBe(1250);
    expect(parseKgToGrams('0,5')).toBe(500);
    expect(parseKgToGrams('2')).toBe(2000);
    expect(parseKgToGrams(' 0,001 ')).toBe(1);
    expect(parseKgToGrams('12,345')).toBe(12345);
    expect(parseKgToGrams('1,2 kg')).toBe(1200);
  });

  it('avoids floating-point drift', () => {
    // 1.005 * 1000 === 1004.9999999999999 in floating point
    expect(parseKgToGrams('1,005')).toBe(1005);
    expect(parseKgToGrams('0,29')).toBe(290);
  });

  it('rejects invalid input', () => {
    for (const bad of ['', ' ', 'abc', '-1', '1,2345', '1.234,5', '1,', ',5', null, undefined]) {
      expect(parseKgToGrams(bad as string)).toBeNull();
    }
  });
});

describe('parseCm', () => {
  it('accepts whole centimetres 1–500', () => {
    expect(parseCm('1')).toBe(1);
    expect(parseCm(' 30 ')).toBe(30);
    expect(parseCm('500')).toBe(500);
  });

  it('rejects zero, fractions, out of range and text', () => {
    for (const bad of ['0', '501', '12,5', '1.5', '-3', 'x', '']) expect(parseCm(bad)).toBeNull();
  });
});

const profile = (over: Partial<CarrierProfile> = {}): CarrierProfile => ({
  carrier: 'Simulated',
  standardLiabilityMinor: 50000,
  maxInsuredValueMinor: 250000,
  maxWeightGrams: 31500,
  maxLengthCm: 120,
  acceptsLithiumInEquipment: true,
  acceptsStandaloneLithiumBatteries: false,
  ...over,
});

describe('insuranceRequired', () => {
  it("is required strictly above the selected carrier's standard liability", () => {
    expect(insuranceRequired(50000, profile())).toBe(false);
    expect(insuranceRequired(50001, profile())).toBe(true);
    expect(insuranceRequired(0, profile())).toBe(false);
    expect(insuranceRequired(50001, profile({ standardLiabilityMinor: 100000 }))).toBe(false);
    expect(insuranceRequired(100001, profile({ standardLiabilityMinor: 100000 }))).toBe(true);
  });

  it('requires nothing before a carrier is selected', () => {
    expect(insuranceRequired(999999, null)).toBe(false);
    expect(insuranceRequired(999999, undefined)).toBe(false);
  });

  it('knows when a value cannot be insured at all', () => {
    expect(insurable(250000, profile())).toBe(true);
    expect(insurable(250001, profile())).toBe(false);
    expect(insurable(999999, null)).toBe(true);
  });

  it('finds the profile of the selected carrier', () => {
    const profiles = [profile({ carrier: 'Manual' }), profile({ carrier: 'Simulated', maxWeightGrams: 1000 })];
    expect(profileFor(profiles, 'Simulated')?.maxWeightGrams).toBe(1000);
    expect(profileFor(profiles, '')).toBeNull();
    expect(profileFor(profiles, 'Dhl')).toBeNull();
    expect(profileFor(null, 'Manual')).toBeNull();
  });
});

describe('validateShipmentForm', () => {
  const valid: ShipmentFormValue = {
    carrier: 'Simulated',
    weightKg: '1,2',
    lengthCm: '30',
    widthCm: '20',
    heightCm: '10',
    insured: false,
    trackingNumber: '',
  };

  it('accepts a complete form', () => {
    expect(validateShipmentForm(valid, 21396)).toEqual({});
  });

  it('requires a tracking number only for Manual', () => {
    expect(validateShipmentForm({ ...valid, carrier: 'Manual' }, 100).trackingNumber).toBe('shipping.form.trackingRequired');
    expect(validateShipmentForm({ ...valid, carrier: 'Manual', trackingNumber: '00340434' }, 100)).toEqual({});
  });

  it("requires insurance above the carrier's standard liability", () => {
    expect(validateShipmentForm(valid, 60000, profile()).insured).toBe('shipping.form.insuranceRequired');
    expect(validateShipmentForm({ ...valid, insured: true }, 60000, profile())).toEqual({});
    expect(validateShipmentForm(valid, 60000, profile({ standardLiabilityMinor: 100000 }))).toEqual({});
    // Without a profile the server remains the only check.
    expect(validateShipmentForm(valid, 60000)).toEqual({});
  });

  it("checks the carrier's weight and length limits", () => {
    const p = profile({ maxWeightGrams: 1000, maxLengthCm: 25 });
    const errors = validateShipmentForm(valid, 100, p);
    expect(errors.weightKg).toBe('shipping.form.tooHeavy');
    expect(errors.lengthCm).toBe('shipping.form.tooLong');
    expect(errors.widthCm).toBeUndefined();
    expect(validateShipmentForm({ ...valid, weightKg: '1', lengthCm: '25' }, 100, p)).toEqual({});
  });

  it('reports every invalid field', () => {
    const errors = validateShipmentForm({ ...valid, carrier: '', weightKg: '0', lengthCm: '0', widthCm: '600', heightCm: 'x' }, 100);
    expect(Object.keys(errors).sort()).toEqual(['carrier', 'heightCm', 'lengthCm', 'weightKg', 'widthCm']);
  });
});

describe('shipment helpers', () => {
  const shipment = (status: Shipment['status']) => ({ status }) as Shipment;

  it('treats every non-cancelled shipment as open', () => {
    expect(hasOpenShipment([])).toBe(false);
    expect(hasOpenShipment([shipment('Cancelled')])).toBe(false);
    expect(hasOpenShipment([shipment('Cancelled'), shipment('LabelCreated')])).toBe(true);
    expect(hasOpenShipment([shipment('Delivered')])).toBe(true);
  });

  it('sorts the timeline newest first without mutating the input', () => {
    const events: TrackingEvent[] = [
      { occurredAt: '2026-10-15T08:00:00Z', kind: 'PickedUp', description: 'a', location: null },
      { occurredAt: '2026-10-15T10:00:00Z', kind: 'Delivered', description: 'c', location: null },
      { occurredAt: '2026-10-15T09:00:00Z', kind: 'InTransit', description: 'b', location: null },
    ];
    expect(eventsNewestFirst(events).map((e) => e.description)).toEqual(['c', 'b', 'a']);
    expect(events[0].description).toBe('a');
  });
});

describe('remaining time', () => {
  const now = Date.parse('2026-10-15T12:00:00Z');
  const plus = (ms: number) => new Date(now + ms).toISOString();
  const MIN = 60_000;
  const HOUR = 60 * MIN;

  it('splits durations into days, hours and minutes', () => {
    expect(splitDuration(47 * HOUR + 59 * MIN + 59_000)).toEqual({ days: 1, hours: 23, minutes: 59 });
    expect(splitDuration(-5)).toEqual({ days: 0, hours: 0, minutes: 0 });
  });

  it('formats the remaining inspection time', () => {
    expect(formatRemaining(plus(48 * HOUR), now, t)).toBe('days:2 hours:0 minutes:0');
    expect(formatRemaining(plus(25 * HOUR + 5 * MIN), now, t)).toBe('days:1 hours:1 minutes:5');
    expect(formatRemaining(plus(3 * HOUR + 7 * MIN), now, t)).toBe('hours:3 minutes:7');
    expect(formatRemaining(plus(42 * MIN), now, t)).toBe('minutes:42');
    expect(formatRemaining(plus(30_000), now, t)).toBe('lessThanMinute');
  });

  it('reports expired and missing deadlines', () => {
    expect(formatRemaining(plus(0), now, t)).toBe('expired');
    expect(formatRemaining(plus(-HOUR), now, t)).toBe('expired');
    expect(formatRemaining(null, now, t)).toBe('–');
    expect(formatRemaining('not a date', now, t)).toBe('–');
  });

  it('knows whether the inspection is still running', () => {
    expect(inspectionRunning({ status: 'Delivered', inspectionEndsAt: plus(HOUR) }, now)).toBe(true);
    expect(inspectionRunning({ status: 'Delivered', inspectionEndsAt: plus(-HOUR) }, now)).toBe(false);
    expect(inspectionRunning({ status: 'Completed', inspectionEndsAt: plus(HOUR) }, now)).toBe(false);
    expect(inspectionRunning({ status: 'Delivered', inspectionEndsAt: null }, now)).toBe(false);
  });
});
