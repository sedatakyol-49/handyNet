import { DOCUMENT, Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { InvoicesApi } from '../../../core/api/handynet-api';
import { ToastService } from '../../../core/ui-state/toast.service';
import { invoicePdfFileName } from './invoice.logic';

/**
 * Downloads an invoice PDF. It is fetched with the auth headers (Bearer + X-Organization-Id) as a blob – a plain link
 * would not carry them – and saved under the invoice number.
 */
@Injectable()
export class InvoicePdfDownloader {
  private readonly api = inject(InvoicesApi);
  private readonly toast = inject(ToastService);
  private readonly document = inject(DOCUMENT);

  /** Id of the invoice whose PDF is being fetched (button loading state). */
  readonly busyId = signal<string | null>(null);

  async download(invoice: { id: string; number: string }): Promise<void> {
    if (this.busyId()) return;
    this.busyId.set(invoice.id);
    try {
      const blob = await firstValueFrom(this.api.pdf(invoice.id));
      const url = URL.createObjectURL(blob);
      const a = this.document.createElement('a');
      a.href = url;
      a.download = invoicePdfFileName(invoice.number);
      this.document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch (e) {
      this.toast.error(e);
    } finally {
      this.busyId.set(null);
    }
  }
}
