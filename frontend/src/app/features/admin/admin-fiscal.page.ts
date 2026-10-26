import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, of, tap } from 'rxjs';
import { AdminFiscalApi, OrganizationFiscal, RegisterFiscal } from '../../core/api/fiscal-api';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { FiscalForm, FiscalFormErrors, shortSignature, toForm, toRequest, validateFiscalForm } from '../pos/fiscal.logic';
import { AdminTabsComponent } from './admin-tabs';
import { AdminFiskalyConnectionComponent } from './admin-fiskaly-connection';

/**
 * Platform admin "Kassen & TSE": fiscal mode per organization and register (Disabled / simulated test TSE / fiskaly),
 * the simulated TSE's test behaviour (latency, failure rate, forced outage) and its status (serial, counters, last
 * signature, outages). fiskaly credentials are write-only.
 */
@Component({
  selector: 'hn-admin-fiscal-page',
  imports: [FormsModule, AdminTabsComponent, AdminFiskalyConnectionComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'admin.fiscal.subtitle' | t" />
    <hn-admin-tabs />

    <hn-banner class="mb-4" tone="warning" [text]="'admin.fiscal.disclaimer' | t" data-testid="admin-fiscal-disclaimer" />

    <div class="grid gap-4 lg:grid-cols-3">
      <section class="hn-card p-4 lg:col-span-1" aria-labelledby="fiscal-orgs-heading">
        <h2 id="fiscal-orgs-heading" class="mb-2 text-sm">{{ 'admin.fiscal.organizations' | t }}</h2>
        <form class="mb-3 flex gap-2" (ngSubmit)="search.set(searchText.trim())" role="search">
          <label for="fiscal-search" class="sr-only">{{ 'common.search' | t }}</label>
          <input hnInput id="fiscal-search" name="search" type="search" [(ngModel)]="searchText" [placeholder]="'common.search' | t" data-testid="admin-fiscal-search" />
          <button hnButton type="submit">{{ 'common.search' | t }}</button>
        </form>
        <hn-banner class="mb-2" [error]="organizations.error()" />
        <ul class="divide-y divide-slate-100" data-testid="admin-fiscal-organizations">
          @for (o of organizations.value() ?? []; track o.organizationId) {
            <li>
              <button type="button" class="flex w-full items-center justify-between gap-2 px-2 py-2 text-start text-sm hover:bg-slate-50"
                [class.bg-slate-100]="selectedId() === o.organizationId" [attr.aria-current]="selectedId() === o.organizationId ? 'true' : null"
                (click)="select(o.organizationId)" [attr.data-org]="o.name">
                <span class="min-w-0">
                  <span class="block truncate font-medium">{{ o.name }}</span>
                  <span class="block text-xs text-slate-500">{{ o.city }} · {{ 'admin.fiscal.registerCount' | t: { count: o.registerCount } }}</span>
                </span>
                <span class="flex shrink-0 flex-col items-end gap-1">
                  <hn-status-badge [status]="o.mode ?? 'None'" kind="fiscalMode" [tone]="modeTone(o.mode)" />
                  @if (o.openOutages) {
                    <span class="text-xs font-semibold text-red-700">{{ 'admin.fiscal.outageCount' | t: { count: o.openOutages } }}</span>
                  }
                </span>
              </button>
            </li>
          } @empty {
            <li class="py-4 text-center text-sm text-slate-500">{{ 'admin.fiscal.noOrganizations' | t }}</li>
          }
        </ul>
      </section>

      <div class="space-y-4 lg:col-span-2">
        @if (!selectedId()) {
          <hn-empty-state [title]="'admin.fiscal.choose' | t" />
        }
        <hn-banner [error]="detail.error()" />
        @if (detail.value(); as d) {
          <section class="hn-card p-4" aria-labelledby="fiscal-org-heading" data-testid="admin-fiscal-detail">
            <h2 id="fiscal-org-heading" class="mb-1 text-sm">{{ d.name }}</h2>
            <p class="mb-3 text-xs text-slate-600">
              {{ 'admin.fiscal.effectiveMode' | t }}: <strong>{{ (d.mode ?? 'None') | tEnum: 'fiscalMode' }}</strong>
              ({{ 'admin.fiscal.source.' + d.modeSource | t }})
            </p>
            @if (d.productionEnvironment && !d.simulatedTseAllowed) {
              <hn-banner class="mb-3" tone="info" [text]="'admin.fiscal.simulatedRefusedInProduction' | t" />
            }
            <form class="grid gap-3 sm:grid-cols-2" (ngSubmit)="saveOrganization(d)" novalidate data-testid="admin-fiscal-org-form">
              <hn-field [label]="'admin.fiscal.mode' | t" for="fiscal-mode" [required]="true" [errors]="err(orgErrors().mode)">
                <select hnSelect id="fiscal-mode" name="mode" [(ngModel)]="orgForm.mode" data-testid="admin-fiscal-mode">
                  <option value="">{{ 'admin.fiscal.choose' | t }}</option>
                  @for (m of modes; track m) {
                    <option [value]="m" [disabled]="m === 'SimulatedTse' && !d.simulatedTseAllowed">{{ m | tEnum: 'fiscalMode' }}</option>
                  }
                </select>
              </hn-field>
              @if (orgForm.mode === 'SimulatedTse') {
                <hn-field [label]="'admin.fiscal.algorithm' | t" for="fiscal-alg" [hint]="'admin.fiscal.algorithmHint' | t">
                  <select hnSelect id="fiscal-alg" name="algorithm" [(ngModel)]="orgForm.algorithm" [disabled]="!!d.simulatedTse">
                    <option value="">ecdsa-plain-SHA256</option>
                    @for (a of d.algorithms; track a) {
                      <option [value]="a">{{ a }}</option>
                    }
                  </select>
                </hn-field>
                <hn-field [label]="'admin.fiscal.latency' | t" for="fiscal-latency" [errors]="err(orgErrors().latencyMs)">
                  <input hnInput id="fiscal-latency" name="latency" inputmode="numeric" [(ngModel)]="orgForm.latencyMs" data-testid="admin-fiscal-latency" />
                </hn-field>
                <hn-field [label]="'admin.fiscal.failureRate' | t" for="fiscal-failure" [errors]="err(orgErrors().failureRatePercent)">
                  <input hnInput id="fiscal-failure" name="failure" inputmode="numeric" [(ngModel)]="orgForm.failureRatePercent" data-testid="admin-fiscal-failure-rate" />
                </hn-field>
                <label class="flex items-center gap-2 text-sm sm:col-span-2">
                  <input type="checkbox" name="outage" [ngModel]="orgForm.forcedOutage === 'true'" (ngModelChange)="orgForm.forcedOutage = $event ? 'true' : 'false'"
                    data-testid="admin-fiscal-outage" />
                  {{ 'admin.fiscal.forcedOutage' | t }}
                </label>
              }
              @if (orgForm.mode === 'FiskalyCloudTse') {
                <hn-banner class="sm:col-span-2" tone="warning" [text]="'admin.fiscal.fiskalyUnverified' | t" />
                <hn-field [label]="'admin.fiscal.tssId' | t" for="fiscal-tss" [errors]="err(orgErrors().fiskalyTssId)">
                  <input hnInput id="fiscal-tss" name="tss" [(ngModel)]="orgForm.fiskalyTssId" autocomplete="off" />
                </hn-field>
                <hn-field [label]="'admin.fiscal.environment' | t" for="fiscal-env">
                  <select hnSelect id="fiscal-env" name="env" [(ngModel)]="orgForm.fiskalyEnvironment">
                    <option value="Sandbox">Sandbox</option>
                    <option value="Production">Production</option>
                  </select>
                </hn-field>
                <hn-field [label]="'admin.fiscal.credentialReference' | t" for="fiscal-ref" [hint]="'admin.fiscal.credentialReferenceHint' | t">
                  <input hnInput id="fiscal-ref" name="ref" [(ngModel)]="orgForm.fiskalyCredentialReference" autocomplete="off" />
                </hn-field>
                <p class="self-end text-xs text-slate-600">
                  {{ (d.configuration?.hasStoredFiskalyCredentials ? 'admin.fiscal.credentialsStored' : 'admin.fiscal.credentialsMissing') | t }}
                </p>
                <hn-field [label]="'admin.fiscal.apiKey' | t" for="fiscal-key">
                  <input hnInput id="fiscal-key" name="key" type="password" [(ngModel)]="orgForm.fiskalyApiKey" autocomplete="new-password" />
                </hn-field>
                <hn-field [label]="'admin.fiscal.apiSecret' | t" for="fiscal-secret" [errors]="err(orgErrors().fiskalyApiSecret)">
                  <input hnInput id="fiscal-secret" name="secret" type="password" [(ngModel)]="orgForm.fiskalyApiSecret" autocomplete="new-password" />
                </hn-field>
                @if (d.configuration?.hasStoredFiskalyCredentials) {
                  <label class="flex items-center gap-2 text-sm sm:col-span-2">
                    <input type="checkbox" name="clear" [(ngModel)]="orgForm.clearFiskalyCredentials" />
                    {{ 'admin.fiscal.clearCredentials' | t }}
                  </label>
                }
              }
              <hn-banner class="sm:col-span-2" [error]="saveError()" />
              <div class="flex justify-end sm:col-span-2">
                <button hnButton variant="primary" type="submit" [loading]="busy()" data-testid="admin-fiscal-save">{{ 'common.save' | t }}</button>
              </div>
            </form>
          </section>

          @if (d.configuration?.mode === 'FiskalyCloudTse') {
            <hn-admin-fiskaly-connection [organizationId]="d.organizationId"
              [configured]="!!d.configuration?.fiskalyTssId && (!!d.configuration?.hasStoredFiskalyCredentials || !!d.configuration?.fiskalyCredentialReference)" />
          }

          @if (d.simulatedTse; as tse) {
            <section class="hn-card p-4" aria-labelledby="fiscal-tse-heading" data-testid="admin-fiscal-tse">
              <h2 id="fiscal-tse-heading" class="mb-2 text-sm">{{ 'admin.fiscal.tseStatus' | t }}</h2>
              <p class="mb-2 text-xs font-semibold text-amber-800">{{ 'pos.tse.testLabel' | t }}</p>
              <dl class="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-sm">
                <dt class="text-slate-600">{{ 'pos.tse.serial' | t }}</dt><dd class="hn-mono break-all text-xs" data-testid="admin-fiscal-serial">{{ tse.serialNumber }}</dd>
                <dt class="text-slate-600">{{ 'pos.tse.algorithm' | t }}</dt><dd>{{ tse.signatureAlgorithm }} · {{ tse.timeFormat }}</dd>
                <dt class="text-slate-600">{{ 'admin.fiscal.transactionCounter' | t }}</dt><dd class="tabular-nums" data-testid="admin-fiscal-tx-counter">{{ tse.transactionCounter }}</dd>
                <dt class="text-slate-600">{{ 'admin.fiscal.signatureCounter' | t }}</dt><dd class="tabular-nums" data-testid="admin-fiscal-sig-counter">{{ tse.signatureCounter }}</dd>
                <dt class="text-slate-600">{{ 'admin.fiscal.publicKey' | t }}</dt><dd class="hn-mono break-all text-xs">{{ tse.publicKey }}</dd>
                @if (tse.lastTransaction; as last) {
                  <dt class="text-slate-600">{{ 'admin.fiscal.lastSignature' | t }}</dt>
                  <dd class="text-xs">
                    #{{ last.transactionNumber }} · {{ last.finishedAt ?? last.startedAt | hnDate }} ·
                    <span class="hn-mono" [attr.title]="last.signature">{{ short(last.signature) }}</span>
                  </dd>
                }
              </dl>
            </section>
          }

          <section class="hn-card p-4" aria-labelledby="fiscal-registers-heading">
            <h2 id="fiscal-registers-heading" class="mb-2 text-sm">{{ 'admin.fiscal.registers' | t }}</h2>
            <hn-table>
              <table class="hn-table" data-testid="admin-fiscal-registers">
                <thead>
                  <tr>
                    <th scope="col">{{ 'admin.fiscal.register' | t }}</th>
                    <th scope="col">{{ 'admin.fiscal.mode' | t }}</th>
                    <th scope="col">{{ 'pos.tse.clientId' | t }}</th>
                    <th scope="col">{{ 'admin.fiscal.behaviour' | t }}</th>
                    <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (r of d.registers; track r.registerId) {
                    <tr [attr.data-register]="r.name">
                      <td>{{ r.name }}</td>
                      <td>
                        {{ (r.mode ?? 'None') | tEnum: 'fiscalMode' }}
                        <span class="block text-xs text-slate-500">{{ 'admin.fiscal.source.' + r.modeSource | t }}</span>
                      </td>
                      <td class="hn-mono break-all text-xs">{{ r.clientId }}</td>
                      <td class="text-xs">
                        @if (r.mode === 'SimulatedTse') {
                          {{ 'admin.fiscal.behaviourSummary' | t: { latency: r.latencyMs, rate: r.failureRatePercent } }}
                          @if (r.forcedOutage) {
                            <span class="block font-semibold text-red-700">{{ 'admin.fiscal.forcedOutage' | t }}</span>
                          }
                        }
                        @if (r.openOutage) {
                          <span class="block font-semibold text-red-700" data-testid="admin-fiscal-register-outage">{{ 'pos.tse.state.Outage' | t }}</span>
                        }
                      </td>
                      <td class="text-end">
                        <button hnButton size="sm" type="button" (click)="editRegister(r)">{{ 'common.edit' | t }}</button>
                      </td>
                    </tr>
                  } @empty {
                    <tr><td colspan="5" class="py-4 text-center text-slate-500">{{ 'admin.fiscal.noRegisters' | t }}</td></tr>
                  }
                </tbody>
              </table>
            </hn-table>

            @if (editing(); as r) {
              <form class="mt-3 grid gap-3 rounded-lg border border-slate-200 p-3 sm:grid-cols-2" (ngSubmit)="saveRegister(d, r)" novalidate
                data-testid="admin-fiscal-register-form">
                <p class="text-sm font-semibold sm:col-span-2">{{ 'admin.fiscal.overrideFor' | t: { name: r.name } }}</p>
                <p class="text-xs text-slate-600 sm:col-span-2">{{ 'admin.fiscal.inheritHint' | t }}</p>
                <hn-field [label]="'admin.fiscal.mode' | t" for="reg-mode">
                  <select hnSelect id="reg-mode" name="mode" [(ngModel)]="registerForm.mode">
                    <option value="">{{ 'admin.fiscal.inherit' | t }}</option>
                    @for (m of modes; track m) {
                      <option [value]="m" [disabled]="m === 'SimulatedTse' && !d.simulatedTseAllowed">{{ m | tEnum: 'fiscalMode' }}</option>
                    }
                  </select>
                </hn-field>
                <hn-field [label]="'pos.tse.clientId' | t" for="reg-client" [hint]="'admin.fiscal.clientIdHint' | t">
                  <input hnInput id="reg-client" name="client" [(ngModel)]="registerForm.clientId" autocomplete="off" />
                </hn-field>
                <hn-field [label]="'admin.fiscal.latency' | t" for="reg-latency" [errors]="err(registerErrors().latencyMs)">
                  <input hnInput id="reg-latency" name="latency" inputmode="numeric" [(ngModel)]="registerForm.latencyMs" />
                </hn-field>
                <hn-field [label]="'admin.fiscal.failureRate' | t" for="reg-failure" [errors]="err(registerErrors().failureRatePercent)">
                  <input hnInput id="reg-failure" name="failure" inputmode="numeric" [(ngModel)]="registerForm.failureRatePercent" />
                </hn-field>
                <hn-field [label]="'admin.fiscal.forcedOutage' | t" for="reg-outage">
                  <select hnSelect id="reg-outage" name="outage" [(ngModel)]="registerForm.forcedOutage">
                    <option value="">{{ 'admin.fiscal.inherit' | t }}</option>
                    <option value="true">{{ 'admin.fiscal.yes' | t }}</option>
                    <option value="false">{{ 'admin.fiscal.no' | t }}</option>
                  </select>
                </hn-field>
                <hn-banner class="sm:col-span-2" [error]="saveError()" />
                <div class="flex justify-end gap-2 sm:col-span-2">
                  <button hnButton type="button" (click)="editing.set(null)">{{ 'common.cancel' | t }}</button>
                  <button hnButton variant="primary" type="submit" [loading]="busy()">{{ 'common.save' | t }}</button>
                </div>
              </form>
            }
          </section>

          <section class="hn-card p-4" aria-labelledby="fiscal-outages-heading">
            <h2 id="fiscal-outages-heading" class="mb-2 text-sm">{{ 'admin.fiscal.outages' | t }}</h2>
            <hn-table>
              <table class="hn-table" data-testid="admin-fiscal-outages">
                <thead>
                  <tr>
                    <th scope="col">{{ 'admin.fiscal.register' | t }}</th>
                    <th scope="col">{{ 'admin.fiscal.outageStart' | t }}</th>
                    <th scope="col">{{ 'admin.fiscal.outageEnd' | t }}</th>
                    <th scope="col">{{ 'admin.fiscal.reason' | t }}</th>
                  </tr>
                </thead>
                <tbody>
                  @for (o of d.outages; track o.id) {
                    <tr>
                      <td>{{ o.registerName }}</td>
                      <td class="whitespace-nowrap">{{ o.startedAt | hnDate }}</td>
                      <td class="whitespace-nowrap">
                        @if (o.endedAt) {
                          {{ o.endedAt | hnDate }}
                        } @else {
                          <span class="font-semibold text-red-700">{{ 'admin.fiscal.ongoing' | t }}</span>
                        }
                      </td>
                      <td class="text-xs">{{ o.reason }}</td>
                    </tr>
                  } @empty {
                    <tr><td colspan="4" class="py-4 text-center text-slate-500">{{ 'admin.fiscal.noOutages' | t }}</td></tr>
                  }
                </tbody>
              </table>
            </hn-table>
          </section>
        }
      </div>
    </div>
  `,
})
export class AdminFiscalPage {
  private readonly api = inject(AdminFiscalApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  protected readonly modes = ['Disabled', 'SimulatedTse', 'FiskalyCloudTse'] as const;
  protected searchText = '';
  protected readonly search = signal('');
  protected readonly selectedId = signal<string | null>(null);
  protected readonly organizations = createQuery(() => this.search(), (s) => this.api.organizations(s || undefined));
  /** The form is filled when an organization is loaded (before it is rendered). */
  protected readonly detail = createQuery(
    () => this.selectedId(),
    (id) => (id ? this.api.organization(id).pipe(tap((d) => this.fillForm(d))) : of(null)),
  );

  protected orgForm: FiscalForm = toForm(null);
  protected registerForm: FiscalForm = toForm(null);
  protected readonly orgErrors = signal<FiscalFormErrors>({});
  protected readonly registerErrors = signal<FiscalFormErrors>({});
  protected readonly editing = signal<RegisterFiscal | null>(null);
  protected readonly busy = signal(false);
  protected readonly saveError = signal<unknown>(null);
  protected select(id: string): void {
    this.selectedId.set(id);
    this.editing.set(null);
    this.saveError.set(null);
  }

  private fillForm(d: OrganizationFiscal): void {
    this.orgForm = toForm(d.configuration);
    if (!this.orgForm.fiskalyEnvironment) this.orgForm.fiskalyEnvironment = 'Sandbox';
    this.orgErrors.set({});
  }

  protected modeTone(mode: string | null): 'success' | 'warning' | 'neutral' | 'danger' {
    return mode === 'FiskalyCloudTse' ? 'success' : mode === 'SimulatedTse' ? 'warning' : mode === 'Disabled' ? 'neutral' : 'danger';
  }

  protected err(key: string | undefined): string | null {
    return key ? this.i18n.t(key) : null;
  }

  protected short(signature: string | null): string {
    return shortSignature(signature);
  }

  protected editRegister(r: RegisterFiscal): void {
    this.registerForm = toForm(r.override);
    this.registerErrors.set({});
    this.saveError.set(null);
    this.editing.set(r);
  }

  protected async saveOrganization(d: OrganizationFiscal): Promise<void> {
    const errors = validateFiscalForm(this.orgForm, 'organization');
    this.orgErrors.set(errors);
    if (Object.keys(errors).length) return;
    const request = toRequest(this.orgForm, 'organization');
    if (request.mode !== 'FiskalyCloudTse') {
      request.fiskalyTssId = null;
      request.fiskalyCredentialReference = null;
      request.fiskalyEnvironment = null;
      delete request.fiskalyApiKey;
      delete request.fiskalyApiSecret;
    }
    if (await this.save(() => this.api.setOrganization(d.organizationId, request))) {
      this.orgForm.fiskalyApiKey = '';
      this.orgForm.fiskalyApiSecret = '';
      this.orgForm.clearFiskalyCredentials = false;
    }
  }

  protected async saveRegister(d: OrganizationFiscal, r: RegisterFiscal): Promise<void> {
    const errors = validateFiscalForm(this.registerForm, 'register');
    this.registerErrors.set(errors);
    if (Object.keys(errors).length) return;
    if (await this.save(() => this.api.setRegister(d.organizationId, r.registerId, toRequest(this.registerForm, 'register')))) this.editing.set(null);
  }

  private async save(call: () => ReturnType<AdminFiscalApi['setOrganization']>): Promise<boolean> {
    this.busy.set(true);
    this.saveError.set(null);
    try {
      const updated = await firstValueFrom(call());
      this.detail.set(updated);
      this.organizations.reload();
      this.toast.success(this.i18n.t('admin.fiscal.saved', { name: updated.name }));
      return true;
    } catch (e) {
      this.saveError.set(e);
      return false;
    } finally {
      this.busy.set(false);
    }
  }
}
