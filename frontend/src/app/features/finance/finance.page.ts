import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { PaymentsApi } from '../../core/api/handynet-api';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';
import { ledgerKind, ledgerKindKey, sellerSignedMinor } from '../orders/payment/payment.logic';

/**
 * Seller finances (B2BSales + orders.fulfil): open balance the marketplace owes for paid, not yet paid-out sales,
 * recent payouts and the seller-payable ledger. Payouts happen automatically when an order is Completed.
 */
@Component({
  selector: 'hn-finance-page',
  imports: [RouterLink, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'finance.title' | t" [description]="'finance.subtitle' | t">
      <a hnButton routerLink="/app/finanzen/rechnungen" data-testid="finance-invoices">{{ 'finance.invoicesLink' | t }}</a>
      <button hnButton type="button" [loading]="balance.loading() || ledger.loading()" (click)="refresh()" data-testid="finance-refresh">
        {{ 'common.refresh' | t }}
      </button>
    </hn-page-header>

    <hn-banner class="mb-3" [error]="balance.error()" />

    <div class="mb-4 grid gap-4 lg:grid-cols-3">
      <section class="hn-card p-4" aria-labelledby="balance-heading" data-testid="balance-card">
        <h2 id="balance-heading" class="text-sm">{{ 'finance.openBalance' | t }}</h2>
        <p class="mt-2 text-2xl font-semibold tabular-nums" data-testid="open-balance">
          {{ balance.value() ? (balance.value()!.openBalanceMinor | money: balance.value()!.currency) : '–' }}
        </p>
        <p class="mt-2 text-xs text-slate-600">{{ 'finance.openBalanceHint' | t }}</p>
      </section>

      <section class="lg:col-span-2" aria-labelledby="payouts-heading">
        <h2 id="payouts-heading" class="mb-2 text-sm">{{ 'finance.recentPayouts' | t }}</h2>
        <ul class="grid gap-3 sm:grid-cols-2 md:hidden" data-testid="payouts-cards">
          @for (p of balance.value()?.recentPayouts ?? []; track p.id) {
            <li class="hn-card flex flex-col gap-1 p-4" [attr.data-order-id]="p.orderId">
              <div class="flex items-start justify-between gap-2">
                <a class="hn-link hn-mono text-xs font-semibold" [routerLink]="['/app/bestellungen', p.orderId]" [attr.aria-label]="'finance.openOrderNumber' | t: { number: p.orderNumber }">
                  {{ p.orderNumber }}
                </a>
                <span class="font-semibold tabular-nums">{{ p.amountMinor | money: p.currency }}</span>
              </div>
              <p class="text-xs text-slate-500">{{ p.createdAt | hnDate }}</p>
              <p class="hn-mono break-all text-xs text-slate-600">{{ p.providerTransferId }}</p>
            </li>
          } @empty {
            @if (!balance.loading()) {
              <li class="sm:col-span-2"><div class="hn-card"><hn-empty-state [title]="'finance.noPayouts' | t" [text]="'finance.noPayoutsText' | t" /></div></li>
            }
          }
        </ul>
        <hn-table class="hidden md:block" [loading]="balance.loading()">
          <table class="hn-table" data-testid="payouts-table">
            <thead>
              <tr>
                <th scope="col">{{ 'finance.date' | t }}</th>
                <th scope="col">{{ 'finance.order' | t }}</th>
                <th scope="col">{{ 'finance.transferId' | t }}</th>
                <th scope="col" class="num">{{ 'finance.amount' | t }}</th>
              </tr>
            </thead>
            <tbody>
              @for (p of balance.value()?.recentPayouts ?? []; track p.id) {
                <tr [attr.data-order-id]="p.orderId">
                  <td class="whitespace-nowrap">{{ p.createdAt | hnDate }}</td>
                  <td>
                    <a class="hn-link hn-mono text-xs" [routerLink]="['/app/bestellungen', p.orderId]" [attr.aria-label]="'finance.openOrderNumber' | t: { number: p.orderNumber }">
                      {{ p.orderNumber }}
                    </a>
                  </td>
                  <td class="hn-mono text-xs">{{ p.providerTransferId }}</td>
                  <td class="num font-medium">{{ p.amountMinor | money: p.currency }}</td>
                </tr>
              } @empty {
                @if (!balance.loading()) {
                  <tr><td colspan="4"><hn-empty-state [title]="'finance.noPayouts' | t" [text]="'finance.noPayoutsText' | t" /></td></tr>
                }
              }
            </tbody>
          </table>
        </hn-table>
      </section>
    </div>

    <section aria-labelledby="ledger-heading">
      <h2 id="ledger-heading" class="mb-1 text-sm">{{ 'finance.ledger' | t }}</h2>
      <p class="mb-2 text-xs text-slate-600">{{ 'finance.ledgerHint' | t }}</p>
      <hn-banner class="mb-3" [error]="ledger.error()" />
      <ul class="grid gap-3 sm:grid-cols-2 md:hidden" data-testid="ledger-cards">
        @for (l of ledger.value()?.items ?? []; track l.entryId + l.sourceId + $index) {
          <li class="hn-card flex flex-col gap-1 p-4" [attr.data-kind]="kind(l.amountMinor)">
            <div class="flex items-start justify-between gap-2">
              <span class="text-xs text-slate-500">{{ l.occurredAt | hnDate }} · {{ kindKey(l) | t }}</span>
              <span class="font-semibold tabular-nums" [class]="kind(l.amountMinor) === 'credit' ? 'text-emerald-800' : 'text-slate-900'">
                {{ signed(l.amountMinor) | signedMoney: l.currency }}
              </span>
            </div>
            <p class="text-sm">{{ l.description }}</p>
          </li>
        } @empty {
          @if (!ledger.loading()) {
            <li class="sm:col-span-2"><div class="hn-card"><hn-empty-state [title]="'finance.noLedger' | t" /></div></li>
          }
        }
      </ul>
      <div class="md:hidden">
        <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="ledger.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
      </div>
      <hn-table class="hidden md:block" [loading]="ledger.loading()">
        <table class="hn-table" data-testid="ledger-table">
          <thead>
            <tr>
              <th scope="col">{{ 'finance.date' | t }}</th>
              <th scope="col">{{ 'finance.description' | t }}</th>
              <th scope="col">{{ 'finance.type' | t }}</th>
              <th scope="col" class="num">{{ 'finance.amount' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (l of ledger.value()?.items ?? []; track l.entryId + l.sourceId + $index) {
              <tr [attr.data-kind]="kind(l.amountMinor)">
                <td class="whitespace-nowrap">{{ l.occurredAt | hnDate }}</td>
                <td>{{ l.description }}</td>
                <td>{{ kindKey(l) | t }}</td>
                <td class="num font-medium" [class]="kind(l.amountMinor) === 'credit' ? 'text-emerald-800' : 'text-slate-900'">
                  {{ signed(l.amountMinor) | signedMoney: l.currency }}
                </td>
              </tr>
            } @empty {
              @if (!ledger.loading()) {
                <tr><td colspan="4"><hn-empty-state [title]="'finance.noLedger' | t" /></td></tr>
              }
            }
          </tbody>
        </table>
        <div footer class="border-t border-slate-200 px-2">
          <hn-pagination [page]="page()" [pageSize]="pageSize" [totalCount]="ledger.value()?.totalCount ?? 0" (pageChange)="page.set($event)" />
        </div>
      </hn-table>
    </section>
  `,
})
export class FinancePage {
  private readonly api = inject(PaymentsApi);

  protected readonly page = signal(1);
  protected readonly pageSize = 25;

  protected readonly balance = createQuery(
    () => true,
    () => this.api.balance(),
  );
  protected readonly ledger = createQuery(
    () => this.page(),
    (page) => this.api.ledger(page, this.pageSize),
  );

  protected readonly kind = ledgerKind;
  protected readonly kindKey = ledgerKindKey;
  protected readonly signed = sellerSignedMinor;

  protected refresh(): void {
    this.balance.reload();
    this.ledger.reload();
  }
}
