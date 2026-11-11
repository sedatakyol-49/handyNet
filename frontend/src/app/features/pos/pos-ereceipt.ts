import { ChangeDetectionStrategy, Component, PLATFORM_ID, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { PosApi } from '../../core/api/handynet-api';
import { PosReceiptLink, Receipt } from '../../core/api/models';
import { CompanyBrandingStore } from '../../core/company/company-branding.store';
import { I18nService, LANGUAGES } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';
import { posErrorText } from './pos-error';
import { QrCodeComponent } from './qr-code';

/** Full URL of a public receipt link for the QR code (the API returns a path of the web app). */
export function publicReceiptUrl(origin: string, path: string): string {
  return origin.replace(/\/+$/, '') + (path.startsWith('/') ? path : '/' + path);
}

/** Simple client check before the API validates the address. */
export function looksLikeEmail(value: string): boolean {
  return /^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$/.test(value.trim());
}

/**
 * E-receipt actions for an issued receipt: send it by email (to the attached customer or an entered address; the
 * Notifications outbox sends it) and show a QR code linking to the public, read-only receipt view.
 */
@Component({
  selector: 'hn-pos-ereceipt',
  imports: [QrCodeComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <section class="pos-card space-y-3 p-4" aria-labelledby="pos-ereceipt-heading" data-testid="pos-ereceipt">
      <h2 id="pos-ereceipt-heading" class="text-sm font-bold">{{ 'pos.ereceipt.title' | t }}</h2>
      <form class="flex flex-wrap items-end gap-2" (submit)="$event.preventDefault(); send(false)" novalidate>
        <hn-field class="min-w-0 grow basis-56" [label]="'pos.ereceipt.email' | t" for="pos-ereceipt-email" [errors]="error()">
          <input hnInput id="pos-ereceipt-email" name="email" type="email" autocomplete="off" inputmode="email" class="min-h-11" [value]="email()"
            (input)="email.set($any($event.target).value); error.set(null)" data-testid="pos-ereceipt-email" />
        </hn-field>
        <hn-field class="w-40" [label]="'pos.ereceipt.language' | t" for="pos-ereceipt-language">
          <select hnSelect id="pos-ereceipt-language" name="language" class="min-h-11" [value]="emailLanguage()"
            (change)="language.set($any($event.target).value)" data-testid="pos-ereceipt-language">
            @for (l of languages; track l.code) {
              <option [value]="l.code" [selected]="emailLanguage() === l.code">{{ l.label }}</option>
            }
          </select>
        </hn-field>
        <button hnButton variant="primary" type="submit" class="min-h-11" [loading]="busy()" [disabled]="busy()" data-testid="pos-ereceipt-send">
          {{ 'pos.ereceipt.send' | t }}
        </button>
        @if (receipt().customerId) {
          <button hnButton type="button" class="min-h-11" [disabled]="busy()" (click)="send(true)" data-testid="pos-ereceipt-send-customer">
            {{ 'pos.ereceipt.sendToCustomer' | t }}
          </button>
        }
      </form>
      @if (sentTo(); as to) {
        <p class="text-xs text-pos-ok-text" role="status" data-testid="pos-ereceipt-sent">{{ 'pos.ereceipt.sent' | t: { to } }}</p>
      }

      <div class="border-t border-pos-line pt-3">
        @if (link(); as l) {
          <div class="flex flex-wrap items-center gap-3" data-testid="pos-ereceipt-qr">
            <hn-qr-code [data]="url()!" [label]="'pos.ereceipt.qrLabel' | t" [sizePx]="132" />
            <div class="min-w-0 flex-1 text-xs text-pos-muted">
              <p class="font-semibold text-pos-ink">{{ 'pos.ereceipt.qrHint' | t }}</p>
              <p class="mt-1 break-all" data-testid="pos-ereceipt-url">{{ url() }}</p>
              <p class="mt-1">{{ 'pos.ereceipt.validUntil' | t: { date: (l.expiresAt | hnDate: 'date') } }}</p>
            </div>
          </div>
        } @else {
          <button hnButton type="button" class="min-h-11" [loading]="linkBusy()" [disabled]="linkBusy()" (click)="createLink()" data-testid="pos-ereceipt-show-qr">
            {{ 'pos.ereceipt.showQr' | t }}
          </button>
        }
        @if (linkError()) {
          <p class="mt-2 text-xs text-pos-danger" role="alert">{{ linkError() }}</p>
        }
      </div>
    </section>
  `,
})
export class PosEReceiptComponent {
  private readonly api = inject(PosApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  readonly receipt = input.required<Receipt>();
  /** Full URL of the public receipt link once the cashier created it (the customer display shows it as a QR code). */
  readonly linkUrl = output<string>();
  private readonly branding = inject(CompanyBrandingStore);
  protected readonly languages = LANGUAGES;
  /** Chosen for this send; null = the company's email language. */
  protected readonly language = signal<string | null>(null);
  protected readonly emailLanguage = computed(() => this.language() ?? this.branding.profile()?.emailLanguage ?? 'de');

  protected readonly email = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly sentTo = signal<string | null>(null);
  protected readonly link = signal<PosReceiptLink | null>(null);
  protected readonly linkBusy = signal(false);
  protected readonly linkError = signal<string | null>(null);
  protected readonly url = computed(() => {
    const l = this.link();
    return l && this.browser ? publicReceiptUrl(window.location.origin, l.path) : null;
  });

  constructor() {
    // Another receipt: fresh state.
    effect(() => {
      this.receipt().id;
      untracked(() => {
        this.email.set('');
        this.language.set(null);
        this.error.set(null);
        this.sentTo.set(null);
        this.link.set(null);
        this.linkError.set(null);
      });
    });
  }

  protected async send(toCustomer: boolean): Promise<void> {
    const address = toCustomer ? null : this.email().trim();
    if (address !== null && !looksLikeEmail(address)) {
      this.error.set(this.i18n.t('pos.ereceipt.invalid'));
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    try {
      const sent = await firstValueFrom(this.api.emailReceipt(this.receipt().id, address, this.emailLanguage()));
      this.sentTo.set(sent.recipientMasked);
      this.email.set('');
      this.toast.success(this.i18n.t('pos.ereceipt.sent', { to: sent.recipientMasked }));
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async createLink(): Promise<void> {
    this.linkBusy.set(true);
    this.linkError.set(null);
    try {
      this.link.set(await firstValueFrom(this.api.receiptLink(this.receipt().id)));
      const url = this.url();
      if (url) this.linkUrl.emit(url);
    } catch (e) {
      this.linkError.set(posErrorText(this.i18n, e));
    } finally {
      this.linkBusy.set(false);
    }
  }
}
