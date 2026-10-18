import qrcode from 'qrcode-generator';
import { ReceiptFiscal } from '../../core/api/models';
import { hasTseBlock, parseQrPayload, qrSvgPath, shortSignature, toForm, toRequest, tseTone, validateFiscalForm } from './fiscal.logic';

const PAYLOAD =
  'V0;4f3c0b2a;Kassenbeleg-V1;Beleg^19.00_0.00_0.00_0.00_0.00^19.00:Bar;12;24;2026-10-18T09:30:00.000Z;2026-10-18T09:30:01.000Z;' +
  'ecdsa-plain-SHA256;unixTime;MEUCIQDsignatureXYZabcdefghijklmnop==;BPublicKeyBase64==';

const fiscal = (status: string): ReceiptFiscal => ({
  status, notice: null, tseSerialNumber: null, transactionNumber: null, signatureCounter: null, signature: null, startedAt: null, finishedAt: null,
});

describe('fiscal logic', () => {
  it('shortens long signatures but keeps short ones', () => {
    expect(shortSignature('abcdefghijklmnopqrstuvwxyz0123456789', 4)).toBe('abcd…6789');
    expect(shortSignature('short')).toBe('short');
    expect(shortSignature(null)).toBe('');
  });

  it('shows the TSE block for signed receipts and outages only', () => {
    expect(hasTseBlock(fiscal('Signed'))).toBe(true);
    expect(hasTseBlock(fiscal('Failed'))).toBe(true);
    expect(hasTseBlock(fiscal('NotConnected'))).toBe(false);
    expect(hasTseBlock(null)).toBe(false);
  });

  it('parses the twelve DSFinV-K QR fields and rejects other payloads', () => {
    const f = parseQrPayload(PAYLOAD)!;
    expect(f.version).toBe('V0');
    expect(f.processType).toBe('Kassenbeleg-V1');
    expect(f.processData).toBe('Beleg^19.00_0.00_0.00_0.00_0.00^19.00:Bar');
    expect([f.transactionNumber, f.signatureCounter]).toEqual(['12', '24']);
    expect(f.timeFormat).toBe('unixTime');
    expect(f.publicKey).toBe('BPublicKeyBase64==');
    expect(parseQrPayload('V1;a;b')).toBeNull();
    expect(parseQrPayload(PAYLOAD.replace('V0', 'V9'))).toBeNull();
  });

  it('renders a QR code of the payload as an SVG path (one square per dark module)', () => {
    const { size, path } = qrSvgPath(PAYLOAD);
    const reference = qrcode(0, 'M');
    reference.addData(PAYLOAD, 'Byte');
    reference.make();
    expect(size).toBe(reference.getModuleCount());
    let dark = 0;
    for (let r = 0; r < size; r++) for (let c = 0; c < size; c++) if (reference.isDark(r, c)) dark++;
    expect(path.match(/M/g)!.length).toBe(dark);
    expect(path.startsWith('M0 0h1v1h-1z')).toBe(true); // finder pattern corner is dark
  });

  it('maps TSE states to top bar tones', () => {
    expect(tseTone('Ok')).toBe('ok');
    expect(tseTone('Test')).toBe('test');
    expect(tseTone('Outage')).toBe('danger');
    expect(tseTone('NotConfigured')).toBe('danger');
    expect(tseTone('Disabled')).toBe('muted');
    expect(tseTone(null)).toBe('muted');
  });

  it('validates the admin form like the API', () => {
    const form = toForm(null);
    expect(validateFiscalForm(form, 'organization').mode).toBe('admin.fiscal.validation.modeRequired');
    expect(validateFiscalForm(form, 'register')).toEqual({});
    expect(validateFiscalForm({ ...form, mode: 'SimulatedTse', failureRatePercent: '101' }, 'organization').failureRatePercent).toBeTruthy();
    expect(validateFiscalForm({ ...form, mode: 'SimulatedTse', latencyMs: '-5' }, 'organization').latencyMs).toBeTruthy();
    expect(validateFiscalForm({ ...form, mode: 'FiskalyCloudTse', fiskalyTssId: 'nope' }, 'organization').fiskalyTssId).toBeTruthy();
    expect(validateFiscalForm({ ...form, mode: 'FiskalyCloudTse', fiskalyApiKey: 'k' }, 'organization').fiskalyApiSecret).toBeTruthy();
  });

  it('builds requests: register rows inherit empty fields, credentials only when both are entered', () => {
    const register = toRequest({ ...toForm(null), failureRatePercent: '100', forcedOutage: '', clientId: ' K-2 ' }, 'register');
    expect(register).toEqual({ mode: null, latencyMs: null, failureRatePercent: 100, forcedOutage: null, clientId: 'K-2' });

    const org = toRequest({ ...toForm(null), mode: 'FiskalyCloudTse', fiskalyApiKey: 'key', fiskalyApiSecret: 'secret', forcedOutage: 'true' }, 'organization');
    expect(org.mode).toBe('FiskalyCloudTse');
    expect(org.forcedOutage).toBe(true);
    expect([org.fiskalyApiKey, org.fiskalyApiSecret]).toEqual(['key', 'secret']);
    const cleared = toRequest({ ...toForm(null), mode: 'FiskalyCloudTse', clearFiskalyCredentials: true, fiskalyApiKey: 'x', fiskalyApiSecret: 'y' }, 'organization');
    expect(cleared.clearFiskalyCredentials).toBe(true);
    expect(cleared.fiskalyApiKey).toBeUndefined();
  });

  it('fills the form from a stored configuration', () => {
    const form = toForm({
      mode: 'SimulatedTse', latencyMs: 250, failureRatePercent: 10, forcedOutage: false, algorithm: 'ecdsa-plain-SHA384', clientId: null,
      fiskalyTssId: null, fiskalyEnvironment: null, fiskalyCredentialReference: null, hasStoredFiskalyCredentials: false, updatedAt: '2026-10-18T00:00:00Z',
    });
    expect([form.mode, form.latencyMs, form.failureRatePercent, form.forcedOutage, form.algorithm]).toEqual(['SimulatedTse', '250', '10', 'false', 'ecdsa-plain-SHA384']);
  });
});
