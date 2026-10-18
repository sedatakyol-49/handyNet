import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { Me, PosCashier } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { POS_APPROVAL_HEADER, POS_CASHIER_HEADER, PosCashierStore, posCashierInterceptor } from './pos-cashier.store';
import { canSubmitPin, pinInput } from './pos-pin-dialog';
import { pinFormatError } from './pos-pin-settings';
import { autoLockInput } from './registers.page';
import { approvalPermissionForCode, approvalPermissionsFor, posRights } from './pos-rights';

const cashier: PosCashier = {
  sessionId: 's1',
  registerId: 'r1',
  userId: 'u-staff',
  displayName: 'Ada',
  role: 'Staff',
  permissions: ['pos.sell'],
  maxDiscountPercent: 0,
  expiresAt: '2026-10-18T20:00:00Z',
  autoLockMinutes: 5,
};

const me = (org: string): Me =>
  ({
    userId: 'u-owner',
    email: 'owner@test.de',
    displayName: 'Owner',
    isPlatformAdmin: false,
    emailVerified: true,
    memberships: [
      {
        organizationId: org,
        organizationName: 'Shop',
        role: 'Owner',
        status: 'Verified',
        modules: ['Pos'],
        permissions: ['pos.sell', 'pos.manage', 'pos.discount', 'pos.refund', 'pos.cash_movement', 'pos.close_session', 'pos.price_override'],
        isWholesaler: false,
        isRetailer: true,
        isRepairProvider: false,
        maxDiscountPercent: 100,
      },
    ],
  }) as unknown as Me;

describe('PIN entry helpers', () => {
  it('types at most six digits and supports backspace and clear', () => {
    let pin = '';
    for (const k of ['4', '7', 'x', '1', '1', '2', '3', '9']) pin = pinInput(pin, k);
    expect(pin).toBe('471123');
    expect(pinInput(pin, 'backspace')).toBe('47112');
    expect(pinInput(pin, 'clear')).toBe('');
  });

  it('submits only with a member and 4–6 digits', () => {
    expect(canSubmitPin('471', 'u1')).toBe(false);
    expect(canSubmitPin('4711', null)).toBe(false);
    expect(canSubmitPin('4711', 'u1')).toBe(true);
  });

  it('checks the own PIN form and the register auto-lock input', () => {
    expect(pinFormatError('12a4', '12a4')).toBe('format');
    expect(pinFormatError('4711', '4712')).toBe('mismatch');
    expect(pinFormatError('4711', '4711')).toBeNull();
    expect(autoLockInput('')).toBe(0);
    expect(autoLockInput(' 15 ')).toBe(15);
    expect(autoLockInput('241')).toBeNull();
    expect(autoLockInput('-1')).toBeNull();
  });
});

describe('PosCashierStore and posCashierInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let store: PosCashierStore;

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([posCashierInterceptor])), provideHttpClientTesting()],
    });
    const session = TestBed.inject(SessionStore);
    session.me.set(me('org-1'));
    session.selectOrganization('org-1');
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    store = TestBed.inject(PosCashierStore);
  });

  afterEach(() => http.verify());

  it('applies the cashier rights instead of the device user and remembers the cashier per tab', () => {
    expect(posRights(store.membership()).manage).toBe(true);
    store.signedIn({ token: 'tok', cashier });
    expect(posRights(store.membership())).toMatchObject({ manage: false, discount: false, sell: true });
    expect(store.actingName()).toBe('Ada');
    expect(JSON.parse(sessionStorage.getItem('hn.pos.cashier')!).token).toBe('tok');
    expect(store.boundElsewhere('r2')).toBe(true);
    expect(store.boundElsewhere('r1')).toBe(false);
  });

  it('sends the cashier token to POS endpoints only, never to PIN entry', async () => {
    store.signedIn({ token: 'tok', cashier });
    void firstValueFrom(client.get('/api/pos/registers'));
    expect(http.expectOne('/api/pos/registers').request.headers.get(POS_CASHIER_HEADER)).toBe('tok');
    void firstValueFrom(client.post('/api/pos/registers/r1/cashier', {}));
    expect(http.expectOne('/api/pos/registers/r1/cashier').request.headers.has(POS_CASHIER_HEADER)).toBe(false);
    void firstValueFrom(client.get('/api/catalog/products'));
    expect(http.expectOne('/api/catalog/products').request.headers.has(POS_CASHIER_HEADER)).toBe(false);
  });

  it('locks the till of the bound register when the API refuses the token', async () => {
    store.signedIn({ token: 'tok', cashier });
    const call = firstValueFrom(client.get('/api/pos/sales')).catch((e) => e);
    http.expectOne('/api/pos/sales').flush({ code: 'pos.cashier_invalid' }, { status: 403, statusText: 'Forbidden' });
    await call;
    expect(store.cashier()).toBeNull();
    expect(store.isLocked('r1')).toBe(true);
    // Locked: back to the device user's rights, no header any more.
    void firstValueFrom(client.get('/api/pos/registers'));
    expect(http.expectOne('/api/pos/registers').request.headers.has(POS_CASHIER_HEADER)).toBe(false);
  });

  it('adds an approval to the next approvable write only and uses it up on success', async () => {
    store.signedIn({ token: 'tok', cashier });
    store.approved('r1', {
      token: 'appr', approvalId: 'a1', expiresAt: new Date(Date.now() + 60_000).toISOString(), approverUserId: 'u-m', approverName: 'Cleo',
      permissions: ['pos.discount'],
    });
    // Approved rights apply (the approver has no discount limit).
    expect(posRights(store.membership())).toMatchObject({ discount: true, maxDiscountPercent: 100, refund: false });

    void firstValueFrom(client.get('/api/pos/sales'));
    expect(http.expectOne('/api/pos/sales').request.headers.has(POS_APPROVAL_HEADER)).toBe(false);
    void firstValueFrom(client.post('/api/pos/sales/x/reprints', {}));
    expect(http.expectOne('/api/pos/sales/x/reprints').request.headers.has(POS_APPROVAL_HEADER)).toBe(false);

    const sale = firstValueFrom(client.post('/api/pos/sales', {}));
    const req = http.expectOne('/api/pos/sales');
    expect(req.request.headers.get(POS_APPROVAL_HEADER)).toBe('appr');
    req.flush({ id: 'r' }, { status: 201, statusText: 'Created' });
    await sale;
    expect(store.approval()).toBeNull();
    expect(posRights(store.membership()).discount).toBe(false);
  });

  it('maps line issues and API refusals to the rights a manager must approve', () => {
    expect(approvalPermissionsFor([{ kind: 'discountLimit', maxPercent: 5, maxDiscountMinor: 10 }, { kind: 'priceOverride', expectedMinor: 1 }])).toEqual([
      'pos.price_override',
      'pos.discount',
    ]);
    expect(approvalPermissionForCode('pos.discount_limit_exceeded', 'pos.refund')).toBe('pos.discount');
    expect(approvalPermissionForCode('permission.denied', 'pos.refund')).toBe('pos.refund');
    expect(approvalPermissionForCode('pos.no_open_session', 'pos.refund')).toBeNull();
  });

  it('ignores a cashier of another organization', () => {
    store.signedIn({ token: 'tok', cashier });
    TestBed.inject(SessionStore).me.set(me('org-2'));
    TestBed.inject(SessionStore).selectOrganization('org-2');
    expect(store.cashier()).toBeNull();
    expect(store.token()).toBeNull();
  });
});
