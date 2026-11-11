import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { OrganizationApi } from '../../core/api/handynet-api';
import { ReceiptBranding, ReceiptDocument, ReceiptPaymentCard } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { CompanyBrandingStore } from '../../core/company/company-branding.store';
import { receiptLines } from '../company/company.logic';
import { UI } from '../../shared/ui';
import { hasTseBlock, shortSignature } from './fiscal.logic';
import { QrCodeComponent } from './qr-code';
import { receiptCardLines } from './terminals/card-flow';

/**
 * The receipt exactly as issued (the API's stored document). Marked `hn-print-area`: when printed, only this block is
 * rendered, on a narrow (80 mm) receipt layout (see styles.css). Only one print area may be on a page.
 */
@Component({
  selector: 'hn-receipt-view',
  imports: [...UI, QrCodeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @let d = doc();
    <article class="hn-print-area hn-receipt mx-auto max-w-sm rounded-lg border border-slate-300 bg-white p-4 font-mono text-xs text-slate-900"
      [attr.aria-label]="d.title + ' ' + d.receiptNumber" data-testid="receipt">
      @if (copy()) {
        <p class="mb-2 border-2 border-slate-900 py-1 text-center text-sm font-bold tracking-widest uppercase" data-testid="receipt-copy">
          {{ 'pos.receipt.copy' | t: { sequence: copy()! } }}
        </p>
      }
      @if (d.fiscal.status !== 'Signed') {
        <p class="mb-3 rounded border-2 border-amber-500 bg-amber-50 px-2 py-1.5 text-center text-sm font-bold text-amber-900" role="note" data-testid="receipt-fiscal-notice">
          {{ d.fiscal.notice || ('pos.receipt.notSigned' | t) }}
        </p>
      }
      <header class="mb-2 text-center">
        @if (logo(); as url) {
          <img [src]="url" [alt]="d.seller.name" class="hn-receipt-logo mx-auto mb-1" data-testid="receipt-logo" />
        }
        @for (l of headerLines(); track $index) {
          <p class="min-h-[1em]" data-testid="receipt-header-text">{{ l }}</p>
        }
        <p class="text-sm font-bold">{{ d.seller.name }}</p>
        @if (d.seller.legalName && d.seller.legalName !== d.seller.name) {
          <p>{{ d.seller.legalName }}</p>
        }
        <p>{{ d.seller.street }}</p>
        <p>{{ d.seller.postalCode }} {{ d.seller.city }}</p>
        @if (d.seller.vatId) {
          <p>{{ 'pos.receipt.vatId' | t }}: {{ d.seller.vatId }}</p>
        }
      </header>

      <p class="text-center text-sm font-bold" data-testid="receipt-title">{{ d.title }}</p>
      <dl class="my-2 grid grid-cols-[auto_1fr] gap-x-3">
        <dt>{{ 'pos.receipt.number' | t }}</dt>
        <dd class="text-end font-bold" data-testid="receipt-number">{{ d.receiptNumber }}</dd>
        @if (d.referencedReceiptNumber) {
          <dt>{{ 'pos.receipt.reference' | t }}</dt>
          <dd class="text-end" data-testid="receipt-reference">{{ d.referencedReceiptNumber }}</dd>
        }
        <dt>{{ 'pos.receipt.register' | t }}</dt>
        <dd class="text-end">{{ d.registerName }}</dd>
        @if (cashier()) {
          <dt>{{ 'pos.receipt.cashier' | t }}</dt>
          <dd class="text-end" data-testid="receipt-cashier">{{ cashier() }}</dd>
        }
        <dt>{{ 'pos.receipt.date' | t }}</dt>
        <dd class="text-end">{{ d.issuedAt | hnDate }}</dd>
      </dl>

      @if (d.customer; as c) {
        <section class="mb-2 border-t border-dashed border-slate-400 pt-1" [attr.aria-label]="'pos.receipt.customer' | t" data-testid="receipt-customer">
          <p class="font-semibold">{{ 'pos.receipt.customer' | t }}</p>
          <p data-testid="receipt-customer-name">{{ c.name }}</p>
          @if (c.company) {
            <p>{{ c.company }}</p>
          }
          @if (c.street) {
            <p>{{ c.street }}</p>
          }
          @if (c.postalCode || c.city) {
            <p>{{ c.postalCode }} {{ c.city }}</p>
          }
          @if (c.vatId) {
            <p>{{ 'pos.receipt.vatId' | t }}: {{ c.vatId }}</p>
          }
        </section>
      }

      <table class="w-full border-y border-dashed border-slate-400" data-testid="receipt-lines">
        <caption class="sr-only">{{ 'pos.receipt.lines' | t }}</caption>
        <thead class="sr-only">
          <tr><th scope="col">{{ 'common.product' | t }}</th><th scope="col">{{ 'pos.receipt.amount' | t }}</th><th scope="col">{{ 'pos.receipt.taxLabel' | t }}</th></tr>
        </thead>
        <tbody>
          @for (l of d.lines; track $index) {
            <tr class="align-top">
              <td class="py-1 pe-2">
                <span class="block font-semibold">{{ l.description }}</span>
                @if (l.imei) {
                  <span class="block">IMEI {{ l.imei }}</span>
                }
                <span class="block">{{ l.quantity }} × {{ l.unitPriceMinor | money: d.currency }}</span>
                @if (l.discountMinor) {
                  <span class="block">{{ 'pos.receipt.discount' | t }} {{ -l.discountMinor | money: d.currency }}</span>
                }
                @if (l.note) {
                  <span class="block italic" data-testid="receipt-line-note">{{ l.note }}</span>
                }
              </td>
              <td class="py-1 text-end whitespace-nowrap tabular-nums">{{ l.lineGrossMinor | money: d.currency }}</td>
              <td class="py-1 ps-2 text-end whitespace-nowrap">{{ l.taxLabel }}</td>
            </tr>
          }
        </tbody>
      </table>

      <p class="my-2 flex justify-between text-sm font-bold">
        <span>{{ 'pos.receipt.total' | t }}</span><span class="tabular-nums" data-testid="receipt-total">{{ d.totalGrossMinor | money: d.currency }}</span>
      </p>

      @if (d.payments.length) {
        <dl class="mb-2 grid grid-cols-[1fr_auto] gap-x-3" data-testid="receipt-payments">
          @for (p of d.payments; track $index) {
            @if (p.tenderedMinor !== null && p.tenderedMinor !== p.amountMinor) {
              <dt>{{ 'pos.receipt.tendered' | t: { method: (p.method | tEnum: 'posPaymentMethod') } }}</dt>
              <dd class="text-end tabular-nums">{{ p.tenderedMinor | money: d.currency }}</dd>
            } @else {
              <dt>{{ p.method | tEnum: 'posPaymentMethod' }}</dt>
              <dd class="text-end tabular-nums">{{ p.amountMinor | money: d.currency }}</dd>
            }
            @for (line of cardLines(p.card); track $index) {
              <dt class="col-span-2 text-[11px] leading-tight break-words" data-testid="receipt-card-line">{{ line }}</dt>
            }
          }
          @if (d.roundingMinor) {
            <dt>{{ 'pos.rounding.label' | t }}</dt>
            <dd class="text-end tabular-nums" data-testid="receipt-rounding">{{ d.roundingMinor | signedMoney }}</dd>
          }
          @if (d.changeMinor) {
            <dt class="font-semibold">{{ 'pos.receipt.change' | t }}</dt>
            <dd class="text-end font-semibold tabular-nums" data-testid="receipt-change">{{ d.changeMinor | money: d.currency }}</dd>
          }
        </dl>
      }

      @if (d.vat.length) {
        <table class="mb-2 w-full border-t border-dashed border-slate-400" data-testid="receipt-vat">
          <caption class="py-1 text-start font-semibold">{{ 'pos.receipt.vatBreakdown' | t }}</caption>
          <thead>
            <tr>
              <th scope="col" class="text-start font-normal">{{ 'pos.receipt.rate' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'common.net' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'pos.receipt.vat' | t }}</th>
              <th scope="col" class="text-end font-normal">{{ 'common.gross' | t }}</th>
            </tr>
          </thead>
          <tbody>
            @for (v of d.vat; track $index) {
              <tr>
                <td>{{ v.label }}</td>
                <td class="text-end tabular-nums">{{ v.netMinor | money: d.currency }}</td>
                <td class="text-end tabular-nums">{{ v.vatMinor | money: d.currency }}</td>
                <td class="text-end tabular-nums">{{ v.grossMinor | money: d.currency }}</td>
              </tr>
            }
          </tbody>
        </table>
      }

      @for (n of notes(); track $index) {
        <p class="mb-1 border-t border-dashed border-slate-400 pt-1" data-testid="receipt-note">{{ n }}</p>
      }

      @if (tseBlock()) {
        <section class="mt-2 border-t border-dashed border-slate-400 pt-1" [attr.aria-label]="'pos.tse.blockTitle' | t" data-testid="receipt-tse">
          <p class="font-semibold">{{ 'pos.tse.blockTitle' | t }}</p>
          @if (d.fiscal.isTest) {
            <p class="my-1 border border-slate-900 px-1 text-center font-bold" data-testid="receipt-tse-test">{{ 'pos.tse.testLabel' | t }}</p>
          }
          <dl class="grid grid-cols-[auto_1fr] gap-x-2 break-all">
            @if (d.fiscal.tseSerialNumber) {
              <dt>{{ 'pos.receipt.tseSerial' | t }}</dt><dd data-testid="receipt-tse-serial">{{ d.fiscal.tseSerialNumber }}</dd>
            }
            @if (d.fiscal.clientId) {
              <dt>{{ 'pos.tse.clientId' | t }}</dt><dd>{{ d.fiscal.clientId }}</dd>
            }
            @if (d.fiscal.transactionNumber !== null) {
              <dt>{{ 'pos.receipt.tseTransaction' | t }}</dt><dd data-testid="receipt-tse-transaction">{{ d.fiscal.transactionNumber }}</dd>
            }
            @if (d.fiscal.signatureCounter !== null) {
              <dt>{{ 'pos.receipt.tseCounter' | t }}</dt><dd data-testid="receipt-tse-counter">{{ d.fiscal.signatureCounter }}</dd>
            }
            @if (d.fiscal.startedAt) {
              <dt>{{ 'pos.receipt.tseStart' | t }}</dt><dd>{{ d.fiscal.startedAt | hnDate }}</dd>
            }
            @if (d.fiscal.finishedAt) {
              <dt>{{ 'pos.receipt.tseEnd' | t }}</dt><dd>{{ d.fiscal.finishedAt | hnDate }}</dd>
            }
            @if (d.fiscal.signatureAlgorithm) {
              <dt>{{ 'pos.tse.algorithm' | t }}</dt><dd>{{ d.fiscal.signatureAlgorithm }}</dd>
            }
            @if (d.fiscal.signature) {
              <dt>{{ 'pos.receipt.tseSignature' | t }}</dt><dd data-testid="receipt-tse-signature" [attr.title]="d.fiscal.signature">{{ signature() }}</dd>
            }
          </dl>
          @if (d.fiscal.status === 'Failed') {
            <p class="mt-1 font-bold" data-testid="receipt-tse-outage">{{ 'pos.tse.outageMarker' | t }}</p>
          }
          @if (d.fiscal.qrPayload) {
            <div class="mt-2 flex justify-center">
              <hn-qr-code [data]="d.fiscal.qrPayload" [label]="'pos.tse.qrLabel' | t" [sizePx]="144" />
            </div>
          }
        </section>
      }
      @if (footerLines().length) {
        <footer class="mt-2 border-t border-dashed border-slate-400 pt-1 text-center" data-testid="receipt-footer-text">
          @for (l of footerLines(); track $index) {
            <p class="min-h-[1em]">{{ l }}</p>
          }
        </footer>
      }
    </article>
  `,
})
export class ReceiptViewComponent {
  readonly doc = input.required<ReceiptDocument>();
  /** Who issued the receipt (not part of the stored document). */
  readonly cashier = input<string | null>(null);
  /** Copy number of a recorded reprint ("Kopie n"); null for the original. */
  readonly copy = input<number | null>(null);
  /**
   * Logo and receipt texts of the issuing company. Undefined = the signed-in company's current profile (receipts inside
   * the app); the public e-receipt passes what the API returned for the receipt's company (null = none).
   */
  readonly branding = input<ReceiptBranding | null | undefined>(undefined);
  private readonly api = inject(OrganizationApi);
  private readonly own = inject(CompanyBrandingStore);
  private readonly i18n = inject(I18nService);
  /** Terminal data printed under a card payment (card, terminal, trace, authorisation). */
  protected cardLines(card: ReceiptPaymentCard | null | undefined): string[] {
    return receiptCardLines(card, (key, params) => this.i18n.t(key, params));
  }
  private readonly effectiveBranding = computed(() => {
    const b = this.branding();
    return b === undefined ? this.own.receiptBranding() : b;
  });
  protected readonly logo = computed(() => this.api.assetUrl(this.effectiveBranding()?.logoUrl));
  protected readonly headerLines = computed(() => receiptLines(this.effectiveBranding()?.receiptHeader));
  protected readonly footerLines = computed(() => receiptLines(this.effectiveBranding()?.receiptFooter));
  /** Notes printed below the totals; the fiscal notice is shown prominently at the top instead of repeated here. */
  protected readonly tseBlock = computed(() => hasTseBlock(this.doc().fiscal));
  protected readonly signature = computed(() => shortSignature(this.doc().fiscal.signature));
  protected readonly notes = computed(() => {
    const d = this.doc();
    return d.notes.filter((n) => n !== d.fiscal.notice);
  });
}
