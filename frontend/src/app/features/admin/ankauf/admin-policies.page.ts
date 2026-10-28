import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../../core/api/api-problem';
import { AdminAnkaufApi } from '../../../core/api/handynet-api';
import { ConditionMap, DEVICE_CONDITIONS, DEVICE_DEFECTS, Policy, PolicySettings } from '../../../core/api/models';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { ankaufProblemText } from '../../ankauf/ankauf.logic';
import { AdminTabsComponent } from '../admin-tabs';
import { AnkaufAdminNavComponent } from './ankauf-admin-nav';
import { PolicyForm, browserTimeZone, formatBp, policyState, policyToForm, validatePolicyForm } from './ankauf-admin.logic';

interface OverrideView {
  scope: 'brand' | 'model';
  key: string;
  values: { condition: string; bp: number }[];
}

/** Platform admin: versioned Ankauf pricing policy (view active values, history, create a new immutable version). */
@Component({
  selector: 'hn-admin-policies-page',
  imports: [FormsModule, AdminTabsComponent, AnkaufAdminNavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'ankaufAdmin.policy.subtitle' | t">
      <hn-actions-menu><button hnButton type="button" [loading]="policies.loading()" (click)="policies.reload()">{{ 'common.refresh' | t }}</button></hn-actions-menu>
      @if (!form()) {
        <button hnButton variant="primary" type="button" (click)="startNew()" data-testid="policy-new">{{ 'ankaufAdmin.policy.newVersion' | t }}</button>
      }
    </hn-page-header>
    <hn-admin-tabs />
    <hn-ankauf-admin-nav />
    <hn-banner class="mb-3" [error]="policies.error()" />

    @if (form(); as f) {
      <section class="hn-card mb-4 p-4" aria-labelledby="policy-form-heading">
        <h2 id="policy-form-heading" class="mb-1 text-sm">{{ 'ankaufAdmin.policy.formTitle' | t: { number: nextNumber() } }}</h2>
        <p class="mb-3 text-xs text-slate-600">{{ 'ankaufAdmin.policy.formHint' | t }}</p>
        <form class="space-y-4" (ngSubmit)="save()" novalidate>
          <hn-banner [text]="saveErrorText()" />
          <fieldset>
            <legend class="mb-2 text-xs font-semibold text-slate-700">{{ 'ankaufAdmin.policy.conditions' | t }} <span class="font-normal text-slate-500">({{ 'ankaufAdmin.policy.rangeConditionShort' | t }})</span></legend>
            <div class="grid gap-3 sm:grid-cols-3 lg:grid-cols-5">
              @for (c of conditions; track c) {
                <hn-field [label]="c | tEnum: 'deviceCondition'" [for]="'cond-' + c" [errors]="err('cond-' + c)">
                  <div class="flex items-center gap-1">
                    <input hnInput [id]="'cond-' + c" [name]="'cond-' + c" inputmode="decimal" class="text-end" [(ngModel)]="f.conditions[c]" [attr.aria-invalid]="!!errors()['cond-' + c]" />
                    <span class="text-sm text-slate-500" aria-hidden="true">%</span>
                  </div>
                </hn-field>
              }
            </div>
          </fieldset>
          <fieldset>
            <legend class="mb-2 text-xs font-semibold text-slate-700">{{ 'ankaufAdmin.policy.defects' | t }} <span class="font-normal text-slate-500">({{ 'ankaufAdmin.policy.rangeDeductionShort' | t }})</span></legend>
            <div class="grid gap-3 sm:grid-cols-3 lg:grid-cols-5">
              @for (d of defects; track d) {
                <hn-field [label]="d | tEnum: 'deviceDefect'" [for]="'defect-' + d" [errors]="err('defect-' + d)">
                  <div class="flex items-center gap-1">
                    <input hnInput [id]="'defect-' + d" [name]="'defect-' + d" inputmode="decimal" class="text-end" [(ngModel)]="f.defects[d]" [attr.aria-invalid]="!!errors()['defect-' + d]" />
                    <span class="text-sm text-slate-500" aria-hidden="true">%</span>
                  </div>
                </hn-field>
              }
            </div>
          </fieldset>
          <fieldset>
            <legend class="mb-2 text-xs font-semibold text-slate-700">{{ 'ankaufAdmin.policy.batteryAndMargins' | t }}</legend>
            <div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <hn-field [label]="'ankaufAdmin.policy.batteryThreshold' | t" for="battery-threshold" [hint]="'ankaufAdmin.policy.batteryThresholdHint' | t" [errors]="err('battery-threshold')">
                <div class="flex items-center gap-1">
                  <input hnInput id="battery-threshold" name="battery-threshold" inputmode="numeric" class="text-end" [(ngModel)]="f.batteryThreshold" [attr.aria-invalid]="!!errors()['battery-threshold']" />
                  <span class="text-sm text-slate-500" aria-hidden="true">%</span>
                </div>
              </hn-field>
              <hn-field [label]="'ankaufAdmin.policy.batteryDeduction' | t" for="battery-deduction" [errors]="err('battery-deduction')">
                <div class="flex items-center gap-1">
                  <input hnInput id="battery-deduction" name="battery-deduction" inputmode="decimal" class="text-end" [(ngModel)]="f.batteryDeduction" [attr.aria-invalid]="!!errors()['battery-deduction']" />
                  <span class="text-sm text-slate-500" aria-hidden="true">%</span>
                </div>
              </hn-field>
              <hn-field [label]="'ankaufAdmin.policy.platformMargin' | t" for="platform-margin" [hint]="'ankaufAdmin.policy.rangeMarginShort' | t" [errors]="err('platform-margin')">
                <div class="flex items-center gap-1">
                  <input hnInput id="platform-margin" name="platform-margin" inputmode="decimal" class="text-end" [(ngModel)]="f.platformMargin" [attr.aria-invalid]="!!errors()['platform-margin']" />
                  <span class="text-sm text-slate-500" aria-hidden="true">%</span>
                </div>
              </hn-field>
              <hn-field [label]="'ankaufAdmin.policy.minDealerMargin' | t" for="dealer-margin" [hint]="'ankaufAdmin.policy.rangeMarginShort' | t" [errors]="err('dealer-margin')">
                <div class="flex items-center gap-1">
                  <input hnInput id="dealer-margin" name="dealer-margin" inputmode="decimal" class="text-end" [(ngModel)]="f.minDealerMargin" [attr.aria-invalid]="!!errors()['dealer-margin']" />
                  <span class="text-sm text-slate-500" aria-hidden="true">%</span>
                </div>
              </hn-field>
            </div>
          </fieldset>

          <fieldset>
            <legend class="mb-1 text-xs font-semibold text-slate-700">{{ 'ankaufAdmin.policy.overrides' | t }}</legend>
            <p class="mb-2 text-xs text-slate-600">{{ 'ankaufAdmin.policy.overridesHint' | t }}</p>
            @if (f.overrides.length) {
              <table class="hn-table mb-2 hn-stack">
                <thead>
                  <tr>
                    <th scope="col">{{ 'ankaufAdmin.policy.scope' | t }}</th>
                    <th scope="col">{{ 'ankaufAdmin.policy.key' | t }}</th>
                    <th scope="col">{{ 'common.condition' | t }}</th>
                    <th scope="col">{{ 'ankaufAdmin.policy.adjustment' | t }}</th>
                    <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of f.overrides; track $index; let i = $index) {
                    <tr>
                      <td [attr.data-label]="'ankaufAdmin.policy.scope' | t">
                        <select hnSelect [name]="'ov-scope-' + i" [(ngModel)]="row.scope" [attr.aria-label]="'ankaufAdmin.policy.scope' | t">
                          <option value="brand">{{ 'ankaufAdmin.policy.scopeBrand' | t }}</option>
                          <option value="model">{{ 'ankaufAdmin.policy.scopeModel' | t }}</option>
                        </select>
                      </td>
                      <td [attr.data-label]="'ankaufAdmin.policy.key' | t" class="min-w-64">
                        <input hnInput [name]="'ov-key-' + i" [id]="'override-' + i + '-key'" [(ngModel)]="row.key"
                          [placeholder]="(row.scope === 'brand' ? 'ankaufAdmin.policy.brandPlaceholder' : 'ankaufAdmin.policy.modelPlaceholder') | t"
                          [attr.aria-label]="'ankaufAdmin.policy.key' | t" [attr.aria-invalid]="!!errors()['override-' + i + '-key']"
                          [attr.aria-describedby]="errors()['override-' + i + '-key'] ? 'override-' + i + '-key-error' : null" />
                        @if (errors()['override-' + i + '-key']) {
                          <p class="mt-1 text-xs text-red-700" [id]="'override-' + i + '-key-error'" role="alert">{{ errors()['override-' + i + '-key'] | t }}</p>
                        }
                      </td>
                      <td [attr.data-label]="'common.condition' | t">
                        <select hnSelect [name]="'ov-cond-' + i" [(ngModel)]="row.condition" [attr.aria-label]="'common.condition' | t">
                          @for (c of conditions; track c) {
                            <option [value]="c">{{ c | tEnum: 'deviceCondition' }}</option>
                          }
                        </select>
                      </td>
                      <td [attr.data-label]="'ankaufAdmin.policy.adjustment' | t">
                        <div class="flex items-center gap-1">
                          <input hnInput [name]="'ov-pct-' + i" [id]="'override-' + i + '-percent'" inputmode="decimal" class="w-24 text-end" [(ngModel)]="row.percent"
                            [attr.aria-label]="'ankaufAdmin.policy.adjustment' | t" [attr.aria-invalid]="!!errors()['override-' + i + '-percent']" />
                          <span class="text-sm text-slate-500" aria-hidden="true">%</span>
                        </div>
                        @if (errors()['override-' + i + '-percent']) {
                          <p class="mt-1 text-xs text-red-700" role="alert">{{ errors()['override-' + i + '-percent'] | t }}</p>
                        }
                      </td>
                      <td class="text-end">
                        <button hnButton size="sm" variant="ghost" type="button" (click)="removeOverride(i)">{{ 'common.remove' | t }}</button>
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
            <button hnButton size="sm" type="button" (click)="addOverride()" data-testid="policy-add-override">{{ 'ankaufAdmin.policy.addOverride' | t }}</button>
          </fieldset>

          <hn-field [label]="'ankaufAdmin.policy.effectiveFrom' | t" for="effective-from" class="max-w-xs"
            [hint]="'ankaufAdmin.policy.effectiveFromHint' | t: { zone: timeZone }" [errors]="err('effective-from')">
            <input hnInput id="effective-from" name="effective-from" type="datetime-local" [(ngModel)]="f.effectiveFrom" [attr.aria-invalid]="!!errors()['effective-from']" />
          </hn-field>

          <div class="flex justify-end gap-2">
            <button hnButton type="button" (click)="cancel()">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="submit" [loading]="saving()" [disabled]="saving()" data-testid="policy-save">{{ 'ankaufAdmin.policy.save' | t }}</button>
          </div>
        </form>
      </section>
    }

    <div class="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <section aria-labelledby="policy-values-heading">
        <h2 id="policy-values-heading" class="mb-2 text-sm">
          @if (shown(); as p) {
            {{ 'ankaufAdmin.policy.valuesTitle' | t: { number: p.number } }}
            <hn-status-badge [status]="state(p)" kind="policyState" [tone]="state(p) === 'active' ? 'success' : state(p) === 'scheduled' ? 'info' : 'neutral'" />
          } @else {
            {{ 'ankaufAdmin.policy.noPolicy' | t }}
          }
        </h2>
        @if (shown(); as p) {
          <div class="hn-card overflow-x-auto" data-testid="policy-values">
            <table class="hn-table">
              <tbody>
                @for (c of conditions; track c) {
                  <tr>
                    <th scope="row" class="text-start font-normal">{{ 'ankaufAdmin.policy.conditionRow' | t: { condition: (c | tEnum: 'deviceCondition') } }}</th>
                    <td class="text-end tabular-nums" [attr.data-value]="'cond-' + c">{{ bp(p.settings.conditionAdjustments[c], true) }}</td>
                  </tr>
                }
                @for (d of defects; track d) {
                  <tr>
                    <th scope="row" class="text-start font-normal">{{ 'ankaufAdmin.policy.defectRow' | t: { defect: (d | tEnum: 'deviceDefect') } }}</th>
                    <td class="text-end tabular-nums">{{ bp(-p.settings.defectDeductions[d], true) }}</td>
                  </tr>
                }
                <tr>
                  <th scope="row" class="text-start font-normal">{{ 'ankaufAdmin.policy.batteryRow' | t: { threshold: p.settings.batteryHealthThresholdPercent } }}</th>
                  <td class="text-end tabular-nums">{{ bp(-p.settings.batteryDeduction, true) }}</td>
                </tr>
                <tr>
                  <th scope="row" class="text-start font-normal">{{ 'ankaufAdmin.policy.platformMargin' | t }}</th>
                  <td class="text-end tabular-nums">{{ bp(p.settings.platformMargin) }}</td>
                </tr>
                <tr>
                  <th scope="row" class="text-start font-normal">{{ 'ankaufAdmin.policy.minDealerMargin' | t }}</th>
                  <td class="text-end tabular-nums">{{ bp(p.settings.minDealerMargin) }}</td>
                </tr>
              </tbody>
            </table>
            <div class="border-t border-slate-200 px-3 py-2 text-xs">
              <p class="mb-1 font-semibold text-slate-700">{{ 'ankaufAdmin.policy.overrides' | t }}</p>
              @for (o of overridesOf(p.settings); track o.scope + o.key) {
                <p>
                  <span class="text-slate-500">{{ (o.scope === 'brand' ? 'ankaufAdmin.policy.scopeBrand' : 'ankaufAdmin.policy.scopeModel') | t }}</span>
                  <span class="hn-mono ms-1">{{ o.key }}</span>:
                  @for (v of o.values; track v.condition; let last = $last) {
                    {{ v.condition | tEnum: 'deviceCondition' }} {{ bp(v.bp, true) }}@if (!last) {, }
                  }
                </p>
              } @empty {
                <p class="text-slate-500">{{ 'ankaufAdmin.policy.noOverrides' | t }}</p>
              }
            </div>
          </div>
        }
      </section>

      <section aria-labelledby="policy-history-heading">
        <h2 id="policy-history-heading" class="mb-2 text-sm">{{ 'ankaufAdmin.policy.history' | t }}</h2>
        <hn-table [loading]="policies.loading()">
          <table class="hn-table hn-stack" data-testid="policy-history">
            <thead>
              <tr>
                <th scope="col">{{ 'ankaufAdmin.policy.version' | t }}</th>
                <th scope="col">{{ 'ankaufAdmin.policy.effectiveFrom' | t }}</th>
                <th scope="col">{{ 'ankaufAdmin.policy.createdAt' | t }}</th>
                <th scope="col">{{ 'common.status' | t }}</th>
                <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
              </tr>
            </thead>
            <tbody>
              @for (p of policies.value() ?? []; track p.id) {
                <tr [class.bg-accent-50]="shown()?.id === p.id">
                  <td [attr.data-label]="'ankaufAdmin.policy.version' | t" class="tabular-nums">#{{ p.number }}</td>
                  <td [attr.data-label]="'ankaufAdmin.policy.effectiveFrom' | t" class="whitespace-nowrap text-xs">{{ p.effectiveFrom | hnDate }}</td>
                  <td [attr.data-label]="'ankaufAdmin.policy.createdAt' | t" class="whitespace-nowrap text-xs">{{ p.createdAt | hnDate }}</td>
                  <td [attr.data-label]="'common.status' | t"><hn-status-badge [status]="state(p)" kind="policyState" [tone]="state(p) === 'active' ? 'success' : state(p) === 'scheduled' ? 'info' : 'neutral'" /></td>
                  <td class="text-end">
                    <button hnButton size="sm" variant="ghost" type="button" [attr.aria-pressed]="shown()?.id === p.id" (click)="selectedId.set(p.id)">
                      {{ 'ankaufAdmin.policy.show' | t }}
                    </button>
                  </td>
                </tr>
              } @empty {
                @if (!policies.loading()) {
                  <tr><td colspan="5"><hn-empty-state [title]="'ankaufAdmin.policy.noPolicy' | t" /></td></tr>
                }
              }
            </tbody>
          </table>
        </hn-table>
      </section>
    </div>
  `,
})
export class AdminPoliciesPage {
  private readonly api = inject(AdminAnkaufApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly conditions = DEVICE_CONDITIONS;
  protected readonly defects = DEVICE_DEFECTS;
  protected readonly timeZone = browserTimeZone();

  protected readonly policies = createQuery(
    () => true,
    () => this.api.policies(),
  );
  protected readonly selectedId = signal<string | null>(null);
  protected readonly active = computed(() => (this.policies.value() ?? []).find((p) => p.isActive) ?? null);
  protected readonly shown = computed(() => {
    const list = this.policies.value() ?? [];
    return list.find((p) => p.id === this.selectedId()) ?? this.active() ?? list[0] ?? null;
  });
  protected readonly nextNumber = computed(() => Math.max(0, ...(this.policies.value() ?? []).map((p) => p.number)) + 1);

  protected readonly form = signal<PolicyForm | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  private readonly saveError = signal<unknown>(null);
  protected readonly saveErrorText = computed(() => {
    const e = this.saveError();
    if (!e) return null;
    return ankaufProblemText(toApiProblem(e), (k) => this.i18n.t(k), (k) => this.i18n.has(k), (p) => describeProblem(this.i18n, p));
  });

  protected state(p: Policy): string {
    return policyState(p, new Date());
  }

  protected bp(value: number, signed = false): string {
    return formatBp(value, this.i18n.locale(), signed);
  }

  protected err(id: string): string | null {
    const key = this.errors()[id];
    return key ? this.i18n.t(key) : null;
  }

  protected overridesOf(s: PolicySettings): OverrideView[] {
    const out: OverrideView[] = [];
    for (const [scope, map] of [
      ['brand', s.brandOverrides],
      ['model', s.modelOverrides],
    ] as const) {
      for (const [key, conditions] of Object.entries(map ?? {})) {
        const values = DEVICE_CONDITIONS.filter((c) => (conditions as ConditionMap)[c] !== undefined).map((c) => ({ condition: c, bp: (conditions as ConditionMap)[c]! }));
        out.push({ scope, key, values });
      }
    }
    return out;
  }

  /** Starts from the active (or newest) version so only the changes have to be typed. */
  protected startNew(): void {
    const base = this.active() ?? this.policies.value()?.[0];
    if (!base) return;
    this.errors.set({});
    this.saveError.set(null);
    this.form.set(policyToForm(base.settings));
  }

  protected cancel(): void {
    this.form.set(null);
    this.errors.set({});
    this.saveError.set(null);
  }

  protected addOverride(): void {
    const f = this.form();
    if (!f) return;
    f.overrides.push({ scope: 'brand', key: '', condition: 'Neu', percent: '0' });
    this.form.set({ ...f, overrides: [...f.overrides] });
  }

  protected removeOverride(index: number): void {
    const f = this.form();
    if (!f) return;
    this.form.set({ ...f, overrides: f.overrides.filter((_, i) => i !== index) });
    this.errors.set({});
  }

  protected async save(): Promise<void> {
    const f = this.form();
    if (!f) return;
    const result = validatePolicyForm(f, new Date());
    this.errors.set(result.errors);
    this.saveError.set(null);
    if (!result.settings) return;
    this.saving.set(true);
    try {
      const created = await firstValueFrom(this.api.createPolicy(result.settings, result.effectiveFrom));
      this.toast.success(this.i18n.t('ankaufAdmin.policy.saved', { number: created.number }));
      this.form.set(null);
      this.selectedId.set(created.id);
      this.policies.reload();
    } catch (e) {
      this.saveError.set(e);
    } finally {
      this.saving.set(false);
    }
  }
}
