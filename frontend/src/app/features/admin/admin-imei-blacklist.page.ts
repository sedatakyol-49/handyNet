import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { AdminImeiBlacklistApi } from '../../core/api/handynet-api';
import { BlacklistEntry, BlacklistReason } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { isValidImei, normalizeImei } from '../inventory/manual-entry/manual-entry.logic';
import { AdminTabsComponent } from './admin-tabs';

/**
 * Platform admin: IMEI blacklist (lost / stolen / fraud), ADR-0019. Entries are never deleted, only deactivated with a
 * reason, so the history stays visible; every block and lift is in the audit log. Tenants never see this list; the API
 * only answers yes/no when a device is bought, booked, sold or returned. No external database is connected.
 */
@Component({
  selector: 'hn-admin-imei-blacklist-page',
  imports: [FormsModule, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'blacklist.subtitle' | t">
      <button hnButton variant="primary" type="button" (click)="openAdd()" data-testid="bl-new">{{ 'blacklist.add' | t }}</button>
    </hn-page-header>
    <hn-admin-tabs />

    <hn-banner class="mb-3" tone="info" [text]="'blacklist.externalNote' | t" />

    <div class="mb-3 flex flex-wrap items-end gap-3">
      <hn-field [label]="'blacklist.search' | t" for="bl-search" class="w-full sm:w-56">
        <input hnInput id="bl-search" inputmode="numeric" maxlength="15" class="hn-mono" [ngModel]="search()" (ngModelChange)="search.set($event); page.set(1)" data-testid="bl-search" />
      </hn-field>
      <hn-field [label]="'blacklist.state' | t" for="bl-state" class="w-full sm:w-44">
        <select hnSelect id="bl-state" [ngModel]="state()" (ngModelChange)="state.set($event); page.set(1)" data-testid="bl-state">
          <option value="">{{ 'blacklist.all' | t }}</option>
          <option value="active">{{ 'blacklist.active' | t }}</option>
          <option value="inactive">{{ 'blacklist.inactive' | t }}</option>
        </select>
      </hn-field>
    </div>

    <hn-banner class="mb-3" [error]="entries.error()" />
    <!-- phone: cards; md+: table -->
    <ul class="grid gap-3 md:hidden" data-testid="bl-cards">
      @for (e of entries.value()?.items ?? []; track e.id) {
        <li class="hn-card flex flex-col gap-1 p-4" [attr.data-imei]="e.imei" [attr.data-active]="e.isActive">
          <div class="flex items-start justify-between gap-2">
            <span class="hn-mono text-sm font-semibold"><bdi dir="ltr">{{ e.imei }}</bdi></span>
            <hn-status-badge [status]="e.isActive ? 'active' : 'inactive'" kind="blacklistState" [tone]="e.isActive ? 'danger' : 'neutral'" />
          </div>
          <p class="text-sm">{{ e.reason | tEnum: 'blacklistReason' }} · {{ e.source }}</p>
          @if (e.note) {
            <p class="text-xs text-slate-600">{{ e.note }}</p>
          }
          <p class="text-xs text-slate-500">{{ e.createdAt | hnDate }}</p>
          @if (!e.isActive) {
            <p class="text-xs text-slate-600">{{ e.deactivatedAt | hnDate }} · {{ e.deactivationReason }}</p>
          } @else {
            <div class="mt-1 flex justify-end">
              <button hnButton size="sm" type="button" (click)="openDeactivate(e)" data-testid="bl-deactivate-card"
                [attr.aria-label]="'blacklist.deactivateLabel' | t: { imei: e.imei }">{{ 'blacklist.deactivate' | t }}</button>
            </div>
          }
        </li>
      } @empty {
        @if (!entries.loading()) {
          <li><div class="hn-card"><hn-empty-state [title]="'blacklist.empty' | t" /></div></li>
        }
      }
    </ul>
    <div class="md:hidden">
      <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="entries.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </div>

    <hn-table class="hidden md:block" [loading]="entries.loading()">
      <table class="hn-table" data-testid="bl-table">
        <thead>
          <tr>
            <th scope="col">IMEI</th>
            <th scope="col">{{ 'blacklist.reason' | t }}</th>
            <th scope="col">{{ 'blacklist.source' | t }}</th>
            <th scope="col">{{ 'blacklist.createdAt' | t }}</th>
            <th scope="col">{{ 'blacklist.state' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (e of entries.value()?.items ?? []; track e.id) {
            <tr [attr.data-imei]="e.imei" [attr.data-active]="e.isActive">
              <td class="hn-mono"><bdi dir="ltr">{{ e.imei }}</bdi></td>
              <td>
                {{ e.reason | tEnum: 'blacklistReason' }}
                @if (e.note) {
                  <br /><span class="text-xs text-slate-600">{{ e.note }}</span>
                }
              </td>
              <td>{{ e.source }}</td>
              <td class="whitespace-nowrap text-slate-600">{{ e.createdAt | hnDate }}</td>
              <td>
                @if (e.isActive) {
                  <hn-status-badge status="active" kind="blacklistState" tone="danger" />
                } @else {
                  <hn-status-badge status="inactive" kind="blacklistState" tone="neutral" />
                  <br /><span class="text-xs text-slate-600">{{ e.deactivatedAt | hnDate }} · {{ e.deactivationReason }}</span>
                }
              </td>
              <td class="text-end">
                @if (e.isActive) {
                  <button hnButton size="sm" type="button" (click)="openDeactivate(e)" data-testid="bl-deactivate"
                    [attr.aria-label]="'blacklist.deactivateLabel' | t: { imei: e.imei }">{{ 'blacklist.deactivate' | t }}</button>
                }
              </td>
            </tr>
          } @empty {
            @if (!entries.loading()) {
              <tr><td colspan="6"><hn-empty-state [title]="'blacklist.empty' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
      <div footer class="border-t border-slate-200 px-2">
        <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="entries.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </div>
    </hn-table>

    <hn-dialog [open]="addOpen()" [title]="'blacklist.add' | t" (closed)="addOpen.set(false)">
      <form class="space-y-4" (ngSubmit)="submitAdd()" novalidate>
        <hn-banner [error]="addProblem()" />
        <hn-field label="IMEI" for="bl-imei" [required]="true" [errors]="addErrors('imei')">
          <input hnInput id="bl-imei" name="imei" inputmode="numeric" class="hn-mono" autocomplete="off" [(ngModel)]="form.imei" data-testid="bl-imei" />
        </hn-field>
        <hn-field [label]="'blacklist.reason' | t" for="bl-reason" [required]="true">
          <select hnSelect id="bl-reason" name="reason" [(ngModel)]="form.reason" data-testid="bl-reason">
            @for (r of reasons; track r) {
              <option [value]="r">{{ r | tEnum: 'blacklistReason' }}</option>
            }
          </select>
        </hn-field>
        <hn-field [label]="'blacklist.source' | t" for="bl-source" [required]="true" [hint]="'blacklist.sourceHint' | t" [errors]="addErrors('source')">
          <input hnInput id="bl-source" name="source" maxlength="200" [(ngModel)]="form.source" data-testid="bl-source" />
        </hn-field>
        <hn-field [label]="'blacklist.note' | t" for="bl-note" [errors]="addErrors('note')">
          <textarea hnInput id="bl-note" name="note" rows="2" maxlength="1000" [(ngModel)]="form.note" data-testid="bl-note"></textarea>
        </hn-field>
        <div class="flex justify-end gap-2">
          <button hnButton type="button" (click)="addOpen.set(false)">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="danger" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="bl-submit">{{ 'blacklist.addSubmit' | t }}</button>
        </div>
      </form>
    </hn-dialog>

    <hn-dialog [open]="!!deactivating()" [title]="'blacklist.deactivateTitle' | t" (closed)="deactivating.set(null)">
      @if (deactivating(); as e) {
        <form class="space-y-4" (ngSubmit)="submitDeactivate(e)" novalidate>
          <p class="text-sm">{{ 'blacklist.deactivateText' | t: { imei: e.imei } }}</p>
          <hn-banner [error]="deactivateProblem()" />
          <hn-field [label]="'blacklist.deactivationReason' | t" for="bl-deact" [required]="true" [errors]="addErrors('reason', deactivateProblem())">
            <textarea hnInput id="bl-deact" name="reason" rows="2" maxlength="500" [(ngModel)]="deactivationReason" data-testid="bl-deactivation-reason"></textarea>
          </hn-field>
          <div class="flex justify-end gap-2">
            <button hnButton type="button" (click)="deactivating.set(null)">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="bl-deactivate-submit">{{ 'blacklist.deactivate' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>
  `,
})
export class AdminImeiBlacklistPage {
  private readonly api = inject(AdminImeiBlacklistApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly reasons: BlacklistReason[] = ['Stolen', 'Lost', 'Fraud', 'Other'];
  protected readonly pageSize = 50;
  protected readonly page = signal(1);
  protected readonly search = signal('');
  protected readonly state = signal<'' | 'active' | 'inactive'>('');
  protected readonly busy = signal(false);

  protected readonly entries = createQuery(
    () => ({
      // Only digits are searched; anything else would be refused by the API.
      imei: /^\d{1,15}$/.test(this.search().trim()) ? this.search().trim() : null,
      active: this.state() === '' ? null : this.state() === 'active',
      page: this.page(),
      pageSize: this.pageSize,
    }),
    (q) => this.api.list(q),
  );

  protected readonly addOpen = signal(false);
  protected readonly addProblem = signal<ApiProblem | null>(null);
  protected form = { imei: '', reason: 'Stolen' as BlacklistReason, source: '', note: '' };

  protected readonly deactivating = signal<BlacklistEntry | null>(null);
  protected readonly deactivateProblem = signal<ApiProblem | null>(null);
  protected deactivationReason = '';

  protected addErrors(field: string, problem: ApiProblem | null = this.addProblem()): string[] {
    return fieldErrors(problem, field);
  }

  protected openAdd(): void {
    this.form = { imei: '', reason: 'Stolen', source: '', note: '' };
    this.addProblem.set(null);
    this.addOpen.set(true);
  }

  protected async submitAdd(): Promise<void> {
    const imei = normalizeImei(this.form.imei);
    const errors: Record<string, string[]> = {};
    if (!isValidImei(imei)) errors['imei'] = [this.i18n.t('blacklist.invalidImei')];
    if (!this.form.source.trim()) errors['source'] = [this.i18n.t('blacklist.sourceRequired')];
    if (this.form.reason === 'Other' && !this.form.note.trim()) errors['note'] = [this.i18n.t('blacklist.noteRequired')];
    if (Object.keys(errors).length) {
      this.addProblem.set({ status: 422, title: this.i18n.t('blacklist.checkInput'), code: 'client', errors });
      return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.add({ imei, reason: this.form.reason, source: this.form.source.trim(), note: this.form.note.trim() || null }));
      this.toast.success(this.i18n.t('blacklist.added', { imei }));
      this.addOpen.set(false);
      this.entries.reload();
    } catch (e) {
      this.addProblem.set(toApiProblem(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected openDeactivate(entry: BlacklistEntry): void {
    this.deactivationReason = '';
    this.deactivateProblem.set(null);
    this.deactivating.set(entry);
  }

  protected async submitDeactivate(entry: BlacklistEntry): Promise<void> {
    if (!this.deactivationReason.trim()) {
      this.deactivateProblem.set({ status: 422, title: this.i18n.t('blacklist.reasonRequired'), code: 'client', errors: {} });
      return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.deactivate(entry.id, this.deactivationReason.trim()));
      this.toast.success(this.i18n.t('blacklist.deactivated', { imei: entry.imei }));
      this.deactivating.set(null);
      this.entries.reload();
    } catch (e) {
      this.deactivateProblem.set(toApiProblem(e));
    } finally {
      this.busy.set(false);
    }
  }
}
