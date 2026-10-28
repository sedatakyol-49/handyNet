import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { AdminModerationApi } from '../../../core/api/handynet-api';
import { AdminModerationOffer, AdminModerationSeller, OfferStatus } from '../../../core/api/models';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { AdminTabsComponent } from '../admin-tabs';
import {
  BlockedFilter,
  EMPTY_FILTERS,
  MAX_REASON_LENGTH,
  ModerationFilters,
  bulkTargets,
  chunk,
  normalizeReason,
  pageSelection,
  toQuery,
  toggleId,
  togglePage,
  validateReason,
} from './moderation.logic';

type PendingAction =
  | { kind: 'blockOffer' | 'unblockOffer'; offer: AdminModerationOffer }
  | { kind: 'blockSelected' | 'unblockSelected'; ids: string[] }
  | { kind: 'blockSeller' | 'unblockSeller'; seller: AdminModerationSeller };

/**
 * Platform admin: hide offers from the marketplace — single offers, a selection, or all offers of a seller (also covers
 * offers the seller creates later). Independent of the seller's own offer status; sellers cannot lift a block.
 * `GET/POST /api/admin/marketplace/...`; every action is audited with the optional reason.
 */
@Component({
  selector: 'hn-admin-moderation-page',
  imports: [FormsModule, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'moderation.subtitle' | t" />
    <hn-admin-tabs />

    <!-- Sellers -->
    <section class="mb-6" aria-labelledby="mod-sellers-heading">
      <div class="mb-2 flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 id="mod-sellers-heading" class="text-sm">{{ 'moderation.sellers.heading' | t }}</h2>
          <p class="text-xs text-slate-600">{{ 'moderation.sellers.hint' | t }}</p>
        </div>
        <form class="flex w-full items-end gap-2 md:w-auto" role="search" (submit)="$event.preventDefault(); sellerSearch.set(sellerSearchInput.value.trim())">
          <hn-field [label]="'moderation.sellers.search' | t" for="mod-seller-search" class="w-full md:w-64">
            <input #sellerSearchInput hnInput id="mod-seller-search" type="search" (change)="sellerSearch.set(sellerSearchInput.value.trim())"
              data-testid="mod-seller-search" />
          </hn-field>
        </form>
      </div>
      <hn-banner class="mb-2" [error]="sellers.error()" />
      <ul class="space-y-2 md:hidden" data-testid="mod-sellers-cards">
        @for (s of sellers.value() ?? []; track s.organizationId) {
          <li class="hn-card p-3" [class.border-red-300]="s.blocked" [class.bg-red-50]="s.blocked">
            <div class="flex items-start justify-between gap-2">
              <div class="min-w-0">
                <p class="font-medium wrap-break-word"><bdi>{{ s.name }}</bdi></p>
                <p class="text-xs text-slate-500"><bdi>{{ s.city }}</bdi></p>
              </div>
              <hn-status-badge [status]="s.verification" kind="orgStatus" />
            </div>
            <dl class="mt-2 grid grid-cols-3 gap-2 text-xs">
              <div><dt class="text-slate-500">{{ 'moderation.sellers.offers' | t }}</dt><dd class="font-semibold tabular-nums">{{ s.offerCount }}</dd></div>
              <div><dt class="text-slate-500">{{ 'moderation.sellers.published' | t }}</dt><dd class="font-semibold tabular-nums">{{ s.publishedCount }}</dd></div>
              <div><dt class="text-slate-500">{{ 'moderation.sellers.blockedOffers' | t }}</dt><dd class="font-semibold tabular-nums">{{ s.blockedOfferCount }}</dd></div>
            </dl>
            <div class="mt-2">
              @if (s.blocked) {
                <hn-status-badge status="Blocked" kind="moderation.sellerState" tone="danger" />
                @if (s.blockReason) {
                  <span class="mt-0.5 block text-xs text-slate-600"><bdi>{{ s.blockReason }}</bdi></span>
                }
              } @else {
                <hn-status-badge status="Visible" kind="moderation.sellerState" tone="success" />
              }
            </div>
            <div class="mt-3 flex flex-wrap gap-2">
              <button hnButton size="sm" variant="ghost" type="button" (click)="showSellerOffers(s)" data-testid="mod-card-seller-show">{{ 'moderation.sellers.showOffers' | t }}</button>
              @if (s.blocked) {
                <button hnButton size="sm" type="button" (click)="ask({ kind: 'unblockSeller', seller: s })" [disabled]="busy()" data-testid="mod-card-seller-unblock">{{ 'moderation.sellers.unblockAll' | t }}</button>
              } @else {
                <button hnButton size="sm" variant="danger" type="button" (click)="ask({ kind: 'blockSeller', seller: s })" [disabled]="busy()" data-testid="mod-card-seller-block">{{ 'moderation.sellers.blockAll' | t }}</button>
              }
            </div>
          </li>
        } @empty {
          @if (!sellers.loading()) {
            <li><hn-empty-state [title]="'moderation.sellers.empty' | t" /></li>
          }
        }
      </ul>
      <hn-table class="hidden md:block" [loading]="sellers.loading()" [scrollY]="true">
        <table class="hn-table" data-testid="mod-sellers-table">
          <thead>
            <tr>
              <th scope="col">{{ 'moderation.sellers.name' | t }}</th>
              <th scope="col">{{ 'moderation.sellers.verification' | t }}</th>
              <th scope="col" class="num">{{ 'moderation.sellers.offers' | t }}</th>
              <th scope="col" class="num">{{ 'moderation.sellers.published' | t }}</th>
              <th scope="col" class="num">{{ 'moderation.sellers.blockedOffers' | t }}</th>
              <th scope="col">{{ 'moderation.sellers.state' | t }}</th>
              <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
            </tr>
          </thead>
          <tbody>
            @for (s of sellers.value() ?? []; track s.organizationId) {
              <tr class="focus-within:bg-slate-50" [class.bg-red-50]="s.blocked" [attr.data-seller]="s.name" [attr.data-blocked]="s.blocked">
                <td>
                  <span class="font-medium"><bdi>{{ s.name }}</bdi></span>
                  <span class="block text-xs text-slate-500"><bdi>{{ s.city }}</bdi></span>
                </td>
                <td><hn-status-badge [status]="s.verification" kind="orgStatus" /></td>
                <td class="num" data-testid="mod-seller-offers">{{ s.offerCount }}</td>
                <td class="num">{{ s.publishedCount }}</td>
                <td class="num" data-testid="mod-seller-blocked-count">{{ s.blockedOfferCount }}</td>
                <td>
                  @if (s.blocked) {
                    <hn-status-badge status="Blocked" kind="moderation.sellerState" tone="danger" />
                    @if (s.blockReason) {
                      <span class="mt-0.5 block max-w-xs text-xs text-slate-600" data-testid="mod-seller-reason"><bdi>{{ s.blockReason }}</bdi></span>
                    }
                  } @else {
                    <hn-status-badge status="Visible" kind="moderation.sellerState" tone="success" />
                  }
                </td>
                <td class="text-end whitespace-nowrap">
                  <button hnButton size="sm" variant="ghost" type="button" (click)="showSellerOffers(s)"
                    [attr.aria-label]="('moderation.sellers.showOffers' | t) + ': ' + s.name" data-testid="mod-seller-show">
                    {{ 'moderation.sellers.showOffers' | t }}
                  </button>
                  @if (s.blocked) {
                    <button hnButton size="sm" type="button" (click)="ask({ kind: 'unblockSeller', seller: s })" [disabled]="busy()"
                      [attr.aria-label]="('moderation.sellers.unblockAll' | t) + ': ' + s.name" data-testid="mod-seller-unblock">
                      {{ 'moderation.sellers.unblockAll' | t }}
                    </button>
                  } @else {
                    <button hnButton size="sm" variant="danger" type="button" (click)="ask({ kind: 'blockSeller', seller: s })" [disabled]="busy()"
                      [attr.aria-label]="('moderation.sellers.blockAll' | t) + ': ' + s.name" data-testid="mod-seller-block">
                      {{ 'moderation.sellers.blockAll' | t }}
                    </button>
                  }
                </td>
              </tr>
            } @empty {
              @if (!sellers.loading()) {
                <tr><td colspan="7"><hn-empty-state [title]="'moderation.sellers.empty' | t" /></td></tr>
              }
            }
          </tbody>
        </table>
      </hn-table>
    </section>

    <!-- Offers -->
    <section aria-labelledby="mod-offers-heading">
      <h2 id="mod-offers-heading" class="mb-2 text-sm">{{ 'moderation.offers.heading' | t }}</h2>
      <hn-filter-bar>
      <form class="mb-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-6" role="search" (submit)="$event.preventDefault(); applyFilters()" data-testid="mod-filters">
        <hn-field [label]="'moderation.offers.seller' | t" for="mod-f-seller">
          <select hnSelect id="mod-f-seller" name="seller" [(ngModel)]="form.sellerId" (ngModelChange)="applyFilters()" data-testid="mod-filter-seller">
            <option value="">{{ 'common.all' | t }}</option>
            @for (s of sellers.value() ?? []; track s.organizationId) {
              <option [value]="s.organizationId">{{ s.name }}</option>
            }
          </select>
        </hn-field>
        <hn-field [label]="'common.search' | t" for="mod-f-search" class="lg:col-span-2">
          <input hnInput id="mod-f-search" name="search" type="search" [(ngModel)]="form.search" [placeholder]="'moderation.offers.searchPlaceholder' | t"
            data-testid="mod-filter-search" />
        </hn-field>
        <hn-field [label]="'moderation.offers.brand' | t" for="mod-f-brand">
          <input hnInput id="mod-f-brand" name="brand" [(ngModel)]="form.brand" data-testid="mod-filter-brand" />
        </hn-field>
        <hn-field [label]="'moderation.offers.category' | t" for="mod-f-category">
          <input hnInput id="mod-f-category" name="category" [(ngModel)]="form.category" />
        </hn-field>
        <hn-field [label]="'common.status' | t" for="mod-f-status">
          <select hnSelect id="mod-f-status" name="status" [(ngModel)]="form.status" (ngModelChange)="applyFilters()">
            <option value="">{{ 'common.all' | t }}</option>
            @for (st of statuses; track st) {
              <option [value]="st">{{ st | tEnum: 'offerStatus' }}</option>
            }
          </select>
        </hn-field>
        <hn-field [label]="'moderation.offers.visibility' | t" for="mod-f-blocked">
          <select hnSelect id="mod-f-blocked" name="blocked" [(ngModel)]="form.blocked" (ngModelChange)="applyFilters()" data-testid="mod-filter-blocked">
            @for (b of blockedOptions; track b.value) {
              <option [value]="b.value">{{ b.labelKey | t }}</option>
            }
          </select>
        </hn-field>
        <div class="flex items-end gap-2 lg:col-span-5">
          <button hnButton variant="primary" type="submit" data-testid="mod-filter-apply">{{ 'moderation.offers.apply' | t }}</button>
          <button hnButton variant="ghost" type="button" (click)="resetFilters()">{{ 'moderation.offers.reset' | t }}</button>
        </div>
      </form>
      </hn-filter-bar>

      <div class="mb-2 flex min-h-8 flex-wrap items-center gap-2" data-testid="mod-bulk-bar">
        <span class="text-xs text-slate-600" aria-live="polite" data-testid="mod-selected-count">{{ 'moderation.offers.selected' | t: { count: selected().size } }}</span>
        <button hnButton size="sm" variant="danger" type="button" [disabled]="!targets().block.length || busy()"
          (click)="ask({ kind: 'blockSelected', ids: targets().block })" data-testid="mod-bulk-block">
          {{ 'moderation.offers.blockSelected' | t: { count: targets().block.length } }}
        </button>
        <button hnButton size="sm" type="button" [disabled]="!targets().unblock.length || busy()"
          (click)="ask({ kind: 'unblockSelected', ids: targets().unblock })" data-testid="mod-bulk-unblock">
          {{ 'moderation.offers.unblockSelected' | t: { count: targets().unblock.length } }}
        </button>
        @if (selected().size) {
          <button hnButton size="sm" variant="ghost" type="button" (click)="selected.set(empty())">{{ 'moderation.offers.clearSelection' | t }}</button>
        }
      </div>

      <hn-banner class="mb-2" [error]="offers.error()" />
      <div class="mb-2 flex min-h-11 items-center gap-2 md:hidden">
        <label class="flex items-center gap-2 text-sm">
          <input type="checkbox" class="hn-checkbox size-5" [checked]="headerState() === 'all'" [indeterminate]="headerState() === 'some'"
            [disabled]="!pageIds().length" (change)="selectPage()" data-testid="mod-card-select-page" />
          {{ 'moderation.offers.selectPage' | t }}
        </label>
      </div>
      <ul class="space-y-2 md:hidden" data-testid="mod-offers-cards">
        @for (o of offers.value()?.items ?? []; track o.offerId) {
          <li class="hn-card flex gap-3 p-3" [class.bg-accent-50]="selected().has(o.offerId)">
            <input type="checkbox" class="hn-checkbox mt-1 size-5 shrink-0" [checked]="selected().has(o.offerId)" (change)="toggle(o.offerId)"
              [attr.aria-label]="'moderation.offers.selectRow' | t: { product: o.brand + ' ' + o.productName }" data-testid="mod-card-select" />
            <div class="min-w-0 flex-1">
              <p class="font-medium wrap-break-word"><bdi>{{ o.brand }} {{ o.productName }}</bdi></p>
              <p class="text-xs text-slate-500"><bdi>{{ o.sellerName }}</bdi> · <bdi>{{ o.sellerCity }}</bdi></p>
              <p class="text-xs text-slate-500"><bdi dir="ltr" class="hn-mono">{{ o.supplierSku }}</bdi></p>
              <div class="mt-2 flex flex-wrap items-center gap-2">
                <span class="text-sm font-semibold tabular-nums"><bdi dir="ltr">{{ o.priceMinor | money: o.currency }}</bdi></span>
                <hn-status-badge [status]="o.status" kind="offerStatus" />
                @if (o.moderation === 'Blocked') {
                  <hn-status-badge status="Blocked" kind="moderation.offerState" tone="danger" />
                } @else if (o.sellerBlocked) {
                  <hn-status-badge status="SellerBlocked" kind="moderation.offerState" tone="warning" />
                } @else {
                  <hn-status-badge status="Visible" kind="moderation.offerState" tone="success" />
                }
              </div>
              @if (o.moderation === 'Blocked' && o.moderationReason) {
                <p class="mt-1 text-xs text-slate-600"><bdi>{{ o.moderationReason }}</bdi></p>
              }
              <div class="mt-3">
                @if (o.moderation === 'Blocked') {
                  <button hnButton size="sm" type="button" (click)="ask({ kind: 'unblockOffer', offer: o })" [disabled]="busy()" data-testid="mod-card-offer-unblock">{{ 'moderation.offers.unblock' | t }}</button>
                } @else {
                  <button hnButton size="sm" variant="danger" type="button" (click)="ask({ kind: 'blockOffer', offer: o })" [disabled]="busy()" data-testid="mod-card-offer-block">{{ 'moderation.offers.block' | t }}</button>
                }
              </div>
            </div>
          </li>
        } @empty {
          @if (!offers.loading()) {
            <li><hn-empty-state [title]="'moderation.offers.empty' | t" /></li>
          }
        }
      </ul>
      <hn-pagination class="md:hidden" [page]="page()" [pageSize]="pageSize" [totalCount]="offers.value()?.totalCount ?? 0" (pageChange)="goTo($event)" />
      <hn-table class="hidden md:block" [loading]="offers.loading()">
        <table class="hn-table" data-testid="mod-offers-table">
          <thead>
            <tr>
              <th scope="col" class="w-8">
                <input type="checkbox" class="hn-checkbox focus-visible:outline-2 focus-visible:outline-accent-600" [checked]="headerState() === 'all'"
                  [indeterminate]="headerState() === 'some'" [disabled]="!pageIds().length" (change)="selectPage()"
                  [attr.aria-label]="'moderation.offers.selectPage' | t" data-testid="mod-select-page" />
              </th>
              <th scope="col">{{ 'common.product' | t }}</th>
              <th scope="col">{{ 'moderation.offers.seller' | t }}</th>
              <th scope="col">{{ 'common.sku' | t }}</th>
              <th scope="col" class="num">{{ 'offers.priceNet' | t }}</th>
              <th scope="col">{{ 'moderation.offers.sellerStatus' | t }}</th>
              <th scope="col">{{ 'moderation.offers.moderation' | t }}</th>
              <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
            </tr>
          </thead>
          <tbody>
            @for (o of offers.value()?.items ?? []; track o.offerId) {
              <tr class="focus-within:bg-slate-50" [class.bg-accent-50]="selected().has(o.offerId)" [attr.data-offer]="o.offerId"
                [attr.data-sku]="o.supplierSku" [attr.data-blocked]="o.blocked">
                <td>
                  <input type="checkbox" class="hn-checkbox focus-visible:outline-2 focus-visible:outline-accent-600" [checked]="selected().has(o.offerId)"
                    (change)="toggle(o.offerId)" [attr.aria-label]="'moderation.offers.selectRow' | t: { product: o.brand + ' ' + o.productName }"
                    data-testid="mod-select" />
                </td>
                <td>
                  <span class="font-medium"><bdi>{{ o.brand }} {{ o.productName }}</bdi></span>
                  <span class="block text-xs text-slate-500">
                    <bdi>{{ o.category }}</bdi>
                    @if (o.ean) { · EAN <bdi dir="ltr">{{ o.ean }}</bdi> }
                    @if (o.mpn) { · MPN <bdi dir="ltr">{{ o.mpn }}</bdi> }
                  </span>
                </td>
                <td>
                  <span><bdi>{{ o.sellerName }}</bdi></span>
                  <span class="block text-xs text-slate-500"><bdi>{{ o.sellerCity }}</bdi></span>
                </td>
                <td class="hn-mono text-xs"><bdi dir="ltr">{{ o.supplierSku }}</bdi></td>
                <td class="num"><bdi dir="ltr">{{ o.priceMinor | money: o.currency }}</bdi></td>
                <td><hn-status-badge [status]="o.status" kind="offerStatus" /></td>
                <td data-testid="mod-offer-state">
                  @if (o.moderation === 'Blocked') {
                    <hn-status-badge status="Blocked" kind="moderation.offerState" tone="danger" />
                    @if (o.moderationReason) {
                      <span class="mt-0.5 block max-w-xs text-xs text-slate-600" data-testid="mod-offer-reason"><bdi>{{ o.moderationReason }}</bdi></span>
                    }
                  } @else if (o.sellerBlocked) {
                    <hn-status-badge status="SellerBlocked" kind="moderation.offerState" tone="warning" />
                  } @else {
                    <hn-status-badge status="Visible" kind="moderation.offerState" tone="success" />
                  }
                </td>
                <td class="text-end whitespace-nowrap">
                  @if (o.moderation === 'Blocked') {
                    <button hnButton size="sm" type="button" (click)="ask({ kind: 'unblockOffer', offer: o })" [disabled]="busy()"
                      [attr.aria-label]="('moderation.offers.unblock' | t) + ': ' + o.brand + ' ' + o.productName" data-testid="mod-offer-unblock">
                      {{ 'moderation.offers.unblock' | t }}
                    </button>
                  } @else {
                    <button hnButton size="sm" variant="danger" type="button" (click)="ask({ kind: 'blockOffer', offer: o })" [disabled]="busy()"
                      [attr.aria-label]="('moderation.offers.block' | t) + ': ' + o.brand + ' ' + o.productName" data-testid="mod-offer-block">
                      {{ 'moderation.offers.block' | t }}
                    </button>
                  }
                </td>
              </tr>
            } @empty {
              @if (!offers.loading()) {
                <tr><td colspan="8"><hn-empty-state [title]="'moderation.offers.empty' | t" /></td></tr>
              }
            }
          </tbody>
        </table>
        <div footer class="border-t border-slate-200 px-2">
          <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="offers.value()?.totalCount ?? 0" (pageChange)="goTo($event)" />
        </div>
      </hn-table>
    </section>

    <hn-dialog [open]="!!pending()" [title]="dialogTitle()" (closed)="close()" testId="mod-dialog">
      @if (pending(); as p) {
        <form class="space-y-4" (ngSubmit)="confirm()" novalidate>
          <hn-banner [error]="actionError()" />
          <p class="text-sm" data-testid="mod-dialog-text">{{ dialogText() }}</p>
          @if (p.kind === 'blockSeller') {
            <p class="text-xs text-slate-600">{{ 'moderation.dialog.sellerHint' | t }}</p>
          }
          <hn-field [label]="(isBlock() ? 'moderation.dialog.reason' : 'moderation.dialog.note') | t" for="mod-reason"
            [hint]="'moderation.dialog.reasonHint' | t: { max: maxReason }" [errors]="reasonError()">
            <textarea hnInput id="mod-reason" name="reason" rows="3" [maxlength]="maxReason" [(ngModel)]="reason"
              [attr.aria-invalid]="!!reasonError()" [attr.aria-describedby]="reasonError() ? 'mod-reason-error' : 'mod-reason-hint'"
              data-testid="mod-reason"></textarea>
          </hn-field>
          <div class="flex justify-end gap-2">
            <button hnButton type="button" [disabled]="busy()" (click)="close()">{{ 'common.cancel' | t }}</button>
            <button hnButton [variant]="isBlock() ? 'danger' : 'primary'" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="mod-confirm">
              {{ (isBlock() ? 'moderation.dialog.confirmBlock' : 'moderation.dialog.confirmUnblock') | t }}
            </button>
          </div>
        </form>
      }
    </hn-dialog>
  `,
})
export class AdminModerationPage {
  private readonly api = inject(AdminModerationApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly maxReason = MAX_REASON_LENGTH;
  protected readonly pageSize = 50;
  protected readonly statuses: OfferStatus[] = ['Draft', 'Published', 'Paused', 'Archived'];
  protected readonly blockedOptions: { value: BlockedFilter; labelKey: string }[] = [
    { value: '', labelKey: 'moderation.offers.visibilityAll' },
    { value: 'blocked', labelKey: 'moderation.offers.visibilityBlocked' },
    { value: 'visible', labelKey: 'moderation.offers.visibilityVisible' },
  ];

  protected readonly sellerSearch = signal('');
  protected readonly sellers = createQuery(
    () => this.sellerSearch(),
    (search) => this.api.sellers(search || null),
  );

  /** Form model (edited in place); `filters` is what was applied. */
  protected form: ModerationFilters = { ...EMPTY_FILTERS };
  protected readonly filters = signal<ModerationFilters>({ ...EMPTY_FILTERS });
  protected readonly page = signal(1);
  protected readonly offers = createQuery(
    () => toQuery(this.filters(), this.page(), this.pageSize),
    (q) => this.api.offers(q),
  );

  protected readonly selected = signal<Set<string>>(new Set());
  protected readonly pageIds = computed(() => (this.offers.value()?.items ?? []).map((o) => o.offerId));
  protected readonly headerState = computed(() => pageSelection(this.selected(), this.pageIds()));
  protected readonly targets = computed(() => bulkTargets(this.selected(), this.offers.value()?.items ?? []));

  protected readonly pending = signal<PendingAction | null>(null);
  protected reason = '';
  protected readonly reasonError = signal<string | null>(null);
  protected readonly actionError = signal<unknown>(null);
  protected readonly busy = signal(false);

  protected readonly isBlock = computed(() => this.pending()?.kind.startsWith('block') ?? false);
  protected readonly dialogTitle = computed(() => {
    const p = this.pending();
    return p ? this.i18n.t(`moderation.dialog.title.${p.kind}`) : '';
  });
  protected readonly dialogText = computed(() => {
    const p = this.pending();
    if (!p) return '';
    switch (p.kind) {
      case 'blockOffer':
      case 'unblockOffer':
        return this.i18n.t(`moderation.dialog.text.${p.kind}`, { product: `${p.offer.brand} ${p.offer.productName}`, seller: p.offer.sellerName });
      case 'blockSelected':
      case 'unblockSelected':
        return this.i18n.t(`moderation.dialog.text.${p.kind}`, { count: p.ids.length });
      case 'blockSeller':
      case 'unblockSeller':
        return this.i18n.t(`moderation.dialog.text.${p.kind}`, { seller: p.seller.name, count: p.seller.offerCount });
    }
  });

  protected empty(): Set<string> {
    return new Set();
  }

  protected applyFilters(): void {
    this.filters.set({ ...this.form });
    this.page.set(1);
    this.selected.set(new Set());
  }

  protected resetFilters(): void {
    this.form = { ...EMPTY_FILTERS };
    this.applyFilters();
  }

  protected goTo(page: number): void {
    // The selection belongs to the visible page (bulk actions act on what the admin sees).
    this.selected.set(new Set());
    this.page.set(page);
  }

  protected showSellerOffers(s: AdminModerationSeller): void {
    this.form = { ...EMPTY_FILTERS, sellerId: s.organizationId };
    this.applyFilters();
    if (typeof document !== 'undefined') document.getElementById('mod-offers-heading')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  protected toggle(id: string): void {
    this.selected.set(toggleId(this.selected(), id));
  }

  protected selectPage(): void {
    this.selected.set(togglePage(this.selected(), this.pageIds()));
  }

  protected ask(action: PendingAction): void {
    this.reason = '';
    this.reasonError.set(null);
    this.actionError.set(null);
    this.pending.set(action);
  }

  protected close(): void {
    if (this.busy()) return;
    this.pending.set(null);
  }

  protected async confirm(): Promise<void> {
    const p = this.pending();
    if (!p || this.busy()) return;
    const invalid = validateReason(this.reason);
    this.reasonError.set(invalid ? this.i18n.t(invalid, { max: MAX_REASON_LENGTH }) : null);
    if (invalid) return;
    const reason = normalizeReason(this.reason);

    this.busy.set(true);
    this.actionError.set(null);
    try {
      let doneKey: string;
      let count = 1;
      switch (p.kind) {
        case 'blockOffer':
          await firstValueFrom(this.api.blockOffer(p.offer.offerId, reason));
          doneKey = 'moderation.toast.offerBlocked';
          break;
        case 'unblockOffer':
          await firstValueFrom(this.api.unblockOffer(p.offer.offerId, reason));
          doneKey = 'moderation.toast.offerUnblocked';
          break;
        case 'blockSelected':
        case 'unblockSelected': {
          count = 0;
          for (const part of chunk(p.ids)) {
            const r = await firstValueFrom(
              p.kind === 'blockSelected' ? this.api.blockOffers(part, reason) : this.api.unblockOffers(part, reason),
            );
            count += r.changed;
          }
          doneKey = p.kind === 'blockSelected' ? 'moderation.toast.selectionBlocked' : 'moderation.toast.selectionUnblocked';
          this.selected.set(new Set());
          break;
        }
        case 'blockSeller':
          await firstValueFrom(this.api.blockSeller(p.seller.organizationId, reason));
          doneKey = 'moderation.toast.sellerBlocked';
          break;
        case 'unblockSeller':
          await firstValueFrom(this.api.unblockSeller(p.seller.organizationId, reason));
          doneKey = 'moderation.toast.sellerUnblocked';
          break;
      }
      this.toast.success(this.i18n.t(doneKey, { count }));
      this.pending.set(null);
      this.offers.reload();
      this.sellers.reload();
    } catch (e) {
      this.actionError.set(e);
      // A partly applied chunked selection is visible after reloading.
      this.offers.reload();
    } finally {
      this.busy.set(false);
    }
  }
}
