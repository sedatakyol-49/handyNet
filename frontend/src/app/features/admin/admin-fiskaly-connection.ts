import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { AdminFiscalApi, FiskalyClientCheck, FiskalyConnectionTest } from '../../core/api/fiscal-api';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { fiskalyTestNotes } from '../pos/fiscal.logic';

/**
 * fiskaly part of "Kassen & TSE": "Verbindung testen" (authenticates with the stored credentials, reports TEST/LIVE, the
 * TSS state and the client registration of every till — nothing is signed) and registering a till as fiskaly client with
 * the TSS admin PIN (used once, never stored).
 */
@Component({
  selector: 'hn-admin-fiskaly-connection',
  imports: [FormsModule, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="hn-card p-4" aria-labelledby="fiskaly-heading" data-testid="admin-fiskaly">
      <div class="mb-2 flex flex-wrap items-center justify-between gap-2">
        <h2 id="fiskaly-heading" class="text-sm">{{ 'admin.fiscal.fiskaly.title' | t }}</h2>
        <button hnButton type="button" [loading]="testing()" [disabled]="!configured()" (click)="test()" data-testid="admin-fiskaly-test">
          {{ 'admin.fiscal.fiskaly.test' | t }}
        </button>
      </div>
      <p class="mb-3 text-xs text-slate-600">{{ (configured() ? 'admin.fiscal.fiskaly.testHint' : 'admin.fiscal.fiskaly.saveFirst') | t }}</p>
      <hn-banner class="mb-2" [error]="testError()" />

      @if (result(); as r) {
        <div class="space-y-2" role="status" data-testid="admin-fiskaly-result" [attr.data-status]="r.status">
          @for (n of notes(); track n.key) {
            <hn-banner [tone]="n.tone" [text]="n.key | t: n.params" />
          }
          <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
            @if (r.environment) {
              <dt class="text-slate-600">{{ 'admin.fiscal.fiskaly.environment' | t }}</dt>
              <dd [class.font-semibold]="r.environment === 'TEST'" [class.text-amber-800]="r.environment === 'TEST'" data-testid="admin-fiskaly-env">
                {{ 'admin.fiscal.fiskaly.env.' + r.environment | t }}
              </dd>
            }
            @if (r.tssState) {
              <dt class="text-slate-600">{{ 'admin.fiscal.fiskaly.tssState' | t }}</dt>
              <dd class="hn-mono text-xs" data-testid="admin-fiskaly-tss-state">{{ r.tssState }}</dd>
            }
            @if (r.tssSerialNumber) {
              <dt class="text-slate-600">{{ 'pos.tse.serial' | t }}</dt>
              <dd class="hn-mono break-all text-xs">{{ r.tssSerialNumber }}</dd>
            }
            <dt class="text-slate-600">{{ 'admin.fiscal.fiskaly.checked' | t }}</dt>
            <dd class="text-xs">{{ r.checkedAt | hnDate }}</dd>
          </dl>
          @if (r.clients.length) {
            <h3 class="pt-2 text-xs font-semibold text-slate-700">{{ 'admin.fiscal.fiskaly.clients' | t }}</h3>
            <ul class="divide-y divide-slate-100 text-sm" data-testid="admin-fiskaly-clients">
              @for (c of r.clients; track c.registerId) {
                <li class="flex flex-wrap items-center justify-between gap-2 py-1.5" [attr.data-register]="c.name" [attr.data-state]="c.state">
                  <span class="min-w-0">
                    <span class="block font-medium">{{ c.name }}</span>
                    <span class="hn-mono block break-all text-xs text-slate-500">{{ c.clientId }}</span>
                  </span>
                  <span class="flex items-center gap-2">
                    <span class="text-xs font-semibold" [class.text-emerald-700]="c.state === 'Registered'" [class.text-red-700]="c.state !== 'Registered'">
                      {{ 'admin.fiscal.fiskaly.clientState.' + c.state | t }}
                    </span>
                    @if (c.state === 'NotRegistered') {
                      <button hnButton size="sm" type="button" (click)="startRegistration(c)" data-testid="admin-fiskaly-register">
                        {{ 'admin.fiscal.fiskaly.register' | t }}
                      </button>
                    }
                  </span>
                </li>
              }
            </ul>
          }
        </div>
      }

      @if (registering(); as c) {
        <form class="mt-3 grid gap-3 rounded-lg border border-slate-200 p-3 sm:grid-cols-2" (ngSubmit)="register(c)" novalidate
          data-testid="admin-fiskaly-register-form">
          <p class="text-sm font-semibold sm:col-span-2">{{ 'admin.fiscal.fiskaly.registerTitle' | t: { name: c.name } }}</p>
          <hn-field [label]="'admin.fiscal.fiskaly.adminPin' | t" for="fiskaly-pin" [hint]="'admin.fiscal.fiskaly.adminPinHint' | t" [required]="true">
            <input hnInput id="fiskaly-pin" name="pin" type="password" autocomplete="off" [(ngModel)]="adminPin" data-testid="admin-fiskaly-pin" />
          </hn-field>
          <hn-banner class="sm:col-span-2" [error]="registerError()" />
          <div class="flex justify-end gap-2 sm:col-span-2">
            <button hnButton type="button" (click)="cancelRegistration()">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="submit" [loading]="busy()" [disabled]="adminPin.length < 6">{{ 'admin.fiscal.fiskaly.register' | t }}</button>
          </div>
        </form>
      }
    </section>
  `,
})
export class AdminFiskalyConnectionComponent {
  private readonly api = inject(AdminFiscalApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  readonly organizationId = input.required<string>();
  /** TSS id and credentials are saved for the organization. */
  readonly configured = input(false);

  protected readonly result = signal<FiskalyConnectionTest | null>(null);
  protected readonly notes = computed(() => {
    const r = this.result();
    return r ? fiskalyTestNotes(r) : [];
  });
  protected readonly testing = signal(false);
  protected readonly testError = signal<unknown>(null);
  protected readonly registering = signal<FiskalyClientCheck | null>(null);
  protected readonly registerError = signal<unknown>(null);
  protected readonly busy = signal(false);
  protected adminPin = '';

  constructor() {
    // Another organization: forget the previous result and any PIN entry.
    effect(() => {
      this.organizationId();
      untracked(() => {
        this.result.set(null);
        this.testError.set(null);
        this.cancelRegistration();
      });
    });
  }

  protected async test(): Promise<void> {
    this.testing.set(true);
    this.testError.set(null);
    try {
      this.result.set(await firstValueFrom(this.api.testFiskaly(this.organizationId())));
    } catch (e) {
      this.result.set(null);
      this.testError.set(e);
    } finally {
      this.testing.set(false);
    }
  }

  protected startRegistration(client: FiskalyClientCheck): void {
    this.adminPin = '';
    this.registerError.set(null);
    this.registering.set(client);
  }

  protected cancelRegistration(): void {
    this.adminPin = '';
    this.registering.set(null);
  }

  protected async register(client: FiskalyClientCheck): Promise<void> {
    if (this.adminPin.length < 6) return;
    this.busy.set(true);
    this.registerError.set(null);
    try {
      await firstValueFrom(this.api.registerFiskalyClient(this.organizationId(), client.registerId, this.adminPin));
      this.toast.success(this.i18n.t('admin.fiscal.fiskaly.registered', { name: client.name }));
      this.cancelRegistration();
      await this.test();
    } catch (e) {
      this.registerError.set(e);
    } finally {
      this.adminPin = '';
      this.busy.set(false);
    }
  }
}
