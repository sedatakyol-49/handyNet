import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map, of } from 'rxjs';
import { InventoryApi } from '../../core/api/handynet-api';
import { UnitStatus } from '../../core/api/models';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

/**
 * Serialized devices (IMEI) of the own organization: filter by status / IMEI prefix / product, detail with history
 * (receipt, sale, return). Devices are booked through the Lager entry ("Artikel anlegen") or an Ankauf.
 */
@Component({
  selector: 'hn-units-page',
  imports: [FormsModule, RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'units.title' | t" [description]="'units.subtitle' | t">
      <a hnButton routerLink="/app/lager" data-testid="units-back">{{ 'units.backToStock' | t }}</a>
    </hn-page-header>

    <div class="mb-3 flex flex-wrap items-end gap-3">
      <hn-field [label]="'units.imeiSearch' | t" for="units-imei" class="w-full sm:w-56">
        <input hnInput id="units-imei" name="imei" inputmode="numeric" class="hn-mono" maxlength="15" [ngModel]="imei()"
          (ngModelChange)="imei.set($event); page.set(1)" data-testid="units-imei" />
      </hn-field>
      <hn-field [label]="'units.status' | t" for="units-status" class="w-full sm:w-44">
        <select hnSelect id="units-status" [ngModel]="status()" (ngModelChange)="status.set($event); page.set(1)" data-testid="units-status">
          <option value="">{{ 'units.allStatuses' | t }}</option>
          @for (s of statuses; track s) {
            <option [value]="s">{{ s | tEnum: 'unitStatus' }}</option>
          }
        </select>
      </hn-field>
      @if (productId()) {
        <p class="flex items-center gap-2 pb-2 text-sm">
          {{ 'units.productFilter' | t }}
          <a hnButton size="sm" variant="ghost" routerLink="/app/lager/geraete">{{ 'units.showAll' | t }}</a>
        </p>
      }
    </div>

    <hn-banner class="mb-3" [error]="units.error()" />
    <!-- phone: cards; md+: table -->
    <ul class="grid gap-3 sm:grid-cols-2 md:hidden" data-testid="units-cards">
      @for (u of units.value()?.items ?? []; track u.id) {
        <li class="hn-card flex flex-col gap-1 p-4" [attr.data-imei]="u.imei">
          <div class="flex items-start justify-between gap-2">
            <span class="hn-mono text-sm font-semibold"><bdi dir="ltr">{{ u.imei }}</bdi></span>
            <hn-status-badge [status]="u.status" kind="unitStatus" />
          </div>
          <p class="text-sm">{{ u.brand }} {{ u.productName }}</p>
          <p class="text-xs text-slate-500">{{ u.warehouseCode }} · {{ u.taxScheme | tEnum: 'unitTaxScheme' }} · {{ u.receivedAt | hnDate }}</p>
          <div class="mt-1 flex items-center justify-between gap-2">
            <span class="text-sm tabular-nums"><bdi dir="ltr">{{ u.purchasePriceMinor | money }}</bdi></span>
            <button hnButton size="sm" variant="ghost" type="button" (click)="detailId.set(u.id)" data-testid="units-open-card"
              [attr.aria-label]="'units.openLabel' | t: { imei: u.imei }">{{ 'units.history' | t }}</button>
          </div>
        </li>
      } @empty {
        @if (!units.loading()) {
          <li class="sm:col-span-2"><div class="hn-card"><hn-empty-state [title]="'units.empty' | t" [text]="'units.emptyText' | t" /></div></li>
        }
      }
    </ul>
    <div class="md:hidden">
      <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="units.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
    </div>

    <hn-table class="hidden md:block" [loading]="units.loading()">
      <table class="hn-table" data-testid="units-table">
        <thead>
          <tr>
            <th scope="col">IMEI</th>
            <th scope="col">{{ 'units.product' | t }}</th>
            <th scope="col">{{ 'inventory.warehouse' | t }}</th>
            <th scope="col">{{ 'units.status' | t }}</th>
            <th scope="col">{{ 'units.taxScheme' | t }}</th>
            <th scope="col" class="num">{{ 'units.purchasePrice' | t }}</th>
            <th scope="col">{{ 'units.receivedAt' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (u of units.value()?.items ?? []; track u.id) {
            <tr [attr.data-imei]="u.imei">
              <td class="hn-mono"><bdi dir="ltr">{{ u.imei }}</bdi></td>
              <td>{{ u.brand }} {{ u.productName }}</td>
              <td>{{ u.warehouseCode }}</td>
              <td><hn-status-badge [status]="u.status" kind="unitStatus" /></td>
              <td>{{ u.taxScheme | tEnum: 'unitTaxScheme' }}</td>
              <td class="num"><bdi dir="ltr">{{ u.purchasePriceMinor | money }}</bdi></td>
              <td class="whitespace-nowrap text-slate-600">{{ u.receivedAt | hnDate }}</td>
              <td class="text-end">
                <button hnButton size="sm" variant="ghost" type="button" (click)="detailId.set(u.id)" data-testid="units-open"
                  [attr.aria-label]="'units.openLabel' | t: { imei: u.imei }">{{ 'units.history' | t }}</button>
              </td>
            </tr>
          } @empty {
            @if (!units.loading()) {
              <tr><td colspan="8"><hn-empty-state [title]="'units.empty' | t" [text]="'units.emptyText' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
      <div footer class="border-t border-slate-200 px-2">
        <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="units.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </div>
    </hn-table>

    <hn-dialog [open]="!!detailId()" [title]="'units.detailTitle' | t" (closed)="detailId.set(null)">
      <hn-banner [error]="detail.error()" />
      @if (detail.value(); as d) {
        <dl class="mb-4 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm" data-testid="unit-detail">
          <dt class="text-slate-500">IMEI</dt><dd class="hn-mono"><bdi dir="ltr">{{ d.unit.imei }}</bdi></dd>
          <dt class="text-slate-500">{{ 'units.product' | t }}</dt><dd>{{ d.unit.brand }} {{ d.unit.productName }}</dd>
          <dt class="text-slate-500">{{ 'units.status' | t }}</dt><dd><hn-status-badge [status]="d.unit.status" kind="unitStatus" /></dd>
          <dt class="text-slate-500">{{ 'units.taxScheme' | t }}</dt><dd>{{ d.unit.taxScheme | tEnum: 'unitTaxScheme' }}</dd>
          <dt class="text-slate-500">{{ 'units.purchasePrice' | t }}</dt><dd><bdi dir="ltr">{{ d.unit.purchasePriceMinor | money }}</bdi></dd>
          <dt class="text-slate-500">{{ 'units.source' | t }}</dt><dd class="break-all"><bdi>{{ d.unit.source }}</bdi></dd>
        </dl>
        <h3 class="mb-2 text-sm font-semibold">{{ 'units.history' | t }}</h3>
        <ol class="space-y-1 text-sm" data-testid="unit-history">
          @for (h of d.history; track h.movementId) {
            <li class="flex flex-wrap gap-x-3">
              <span class="whitespace-nowrap text-slate-600">{{ h.occurredAt | hnDate }}</span>
              <span>{{ h.type | tEnum: 'movementType' }}</span>
              <hn-status-badge [status]="h.statusAfter" kind="unitStatus" />
              <span class="break-all text-slate-500"><bdi>{{ h.reference ?? '' }}</bdi></span>
            </li>
          } @empty {
            <li class="text-slate-500">{{ 'units.noHistory' | t: { date: (d.unit.receivedAt | hnDate) } }}</li>
          }
        </ol>
      }
    </hn-dialog>
  `,
})
export class UnitsPage {
  private readonly api = inject(InventoryApi);
  private readonly route = inject(ActivatedRoute);

  protected readonly statuses: UnitStatus[] = ['InStock', 'Reserved', 'Sold', 'Scrapped'];
  protected readonly pageSize = 50;
  protected readonly page = signal(1);
  protected readonly imei = signal('');
  protected readonly status = signal<UnitStatus | ''>('');
  protected readonly productId = toSignal(this.route.queryParamMap.pipe(map((q) => q.get('productId'))), { initialValue: null });
  protected readonly detailId = signal<string | null>(null);

  protected readonly units = createQuery(
    () => ({
      productId: this.productId(),
      status: this.status() || null,
      // Only digits are searched; anything else would be refused by the API.
      imei: /^\d{1,15}$/.test(this.imei().trim()) ? this.imei().trim() : null,
      page: this.page(),
      pageSize: this.pageSize,
    }),
    (q) => this.api.units(q),
  );

  protected readonly detail = createQuery(
    () => this.detailId(),
    (id) => (id ? this.api.unit(id) : of(null)),
  );
}
