import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AnkaufApi } from '../../core/api/handynet-api';
import { Intake } from '../../core/api/models';
import { UI } from '../../shared/ui';

/** The fixed wording of the declaration of ownership, as printed on the Ankaufbeleg (German document, like the PDF). */
export const OWNERSHIP_DECLARATION =
  'Der Verkäufer / die Verkäuferin erklärt, alleiniger Eigentümer / alleinige Eigentümerin des oben genannten Geräts zu sein und ' +
  'darüber frei verfügen zu dürfen. Das Gerät ist frei von Rechten Dritter; es ist insbesondere nicht gestohlen, verloren, gemietet, ' +
  'finanziert, verpfändet oder unter Eigentumsvorbehalt gekauft. Die Angaben zur Person und zum Gerät sind richtig und vollständig.';

/**
 * HTML/print view of the Ankaufbeleg including the seller's on-screen signature. The PDF (`/beleg`) carries the same
 * content including the signature image.
 */
@Component({
  selector: 'hn-beleg-print',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @let i = intake();
    <article class="hn-print-a4 mx-auto max-w-3xl rounded-xl border border-slate-200 bg-white p-6 text-sm text-slate-900 shadow-sm" lang="de" dir="ltr"
      aria-label="Ankaufbeleg" data-testid="beleg-print">
      <header class="flex flex-wrap items-start justify-between gap-3 border-b-2 border-slate-900 pb-3">
        <div>
          <h2 class="text-2xl font-bold tracking-tight">Ankaufbeleg</h2>
          <p class="text-xs text-slate-600">Ankauf eines gebrauchten Mobilgeräts von einer Privatperson</p>
        </div>
        <dl class="text-end text-xs">
          <dt class="text-slate-500">Belegnummer</dt>
          <dd class="hn-mono font-semibold" data-testid="beleg-number">{{ i.belegNumber }}</dd>
          <dt class="mt-1 text-slate-500">Ankaufdatum</dt>
          <dd>{{ i.completedAt | hnDate }}</dd>
        </dl>
      </header>

      <div class="mt-4 grid gap-4 sm:grid-cols-2">
        <section>
          <h3 class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">Käufer (Händler)</h3>
          <p class="font-medium">{{ dealer() }}</p>
        </section>
        <section>
          <h3 class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">Verkäufer/in (Privatperson)</h3>
          @if (i.seller; as s) {
            <p class="font-medium">{{ s.firstName }} {{ s.lastName }}</p>
            <p>{{ s.street }}, {{ s.postalCode }} {{ s.city }}</p>
            <p>geb. {{ s.birthDate | hnDate: 'date' }}@if (s.nationality) { · {{ s.nationality }} }</p>
            <p>{{ s.idDocumentType | tEnum: 'idDocumentType' }} <span class="hn-mono">{{ s.idDocumentNumberMasked }}</span>@if (s.idExpiryDate) { · gültig bis {{ s.idExpiryDate | hnDate: 'date' }} }</p>
          } @else {
            <p>anonymisiert</p>
          }
        </section>
      </div>

      <section class="mt-4">
        <h3 class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">Gerät</h3>
        <table class="w-full text-sm">
          <tbody class="divide-y divide-slate-100">
            <tr><th scope="row" class="w-40 py-1 text-start font-normal text-slate-500">Modell</th><td>{{ i.brand }} {{ i.model }} {{ i.storage }}</td></tr>
            <tr><th scope="row" class="py-1 text-start font-normal text-slate-500">IMEI</th><td class="hn-mono">{{ i.imei }}</td></tr>
            <tr><th scope="row" class="py-1 text-start font-normal text-slate-500">Zustand (geprüft)</th>
              <td>{{ i.inspectedCondition | tEnum: 'deviceCondition' }}@for (d of i.inspectedDefects; track d) { · {{ d | tEnum: 'deviceDefect' }} }</td></tr>
            @if (i.inspectedBatteryHealthPercent !== null) {
              <tr><th scope="row" class="py-1 text-start font-normal text-slate-500">Akkuzustand</th><td>{{ i.inspectedBatteryHealthPercent }} %</td></tr>
            }
          </tbody>
        </table>
      </section>

      <section class="mt-4 flex flex-wrap items-center justify-between gap-3 rounded-lg bg-slate-50 px-4 py-3">
        <div>
          <p class="text-xs text-slate-500">Ankaufpreis</p>
          <p class="text-xl font-bold tabular-nums" data-testid="beleg-amount">{{ i.paidMinor | money }}</p>
        </div>
        <p class="text-end">{{ i.paymentMethod === 'Cash' ? 'Barzahlung' : 'Überweisung' }}</p>
      </section>
      <p class="mt-2 text-xs text-slate-600">
        Ankauf von einer Privatperson: Der Ankaufpreis enthält keine gesondert ausgewiesene Umsatzsteuer. Der Ankauf wird für die
        Differenzbesteuerung (§ 25a UStG) dokumentiert.
      </p>

      <section class="mt-4">
        <h3 class="mb-1 text-xs font-semibold tracking-wide text-slate-500 uppercase">Eigentumserklärung</h3>
        <p class="text-xs leading-relaxed">{{ declaration }}</p>
      </section>

      <section class="mt-6 grid gap-6 sm:grid-cols-2">
        <div>
          <div class="flex h-24 items-end border-b border-slate-400">
            @if (signatureUrl(); as url) {
              <img [src]="url" alt="Unterschrift Verkäufer/in" class="max-h-24" data-testid="beleg-signature" />
            }
          </div>
          <p class="mt-1 text-xs text-slate-600">
            Unterschrift Verkäufer/in
            @if (i.signature; as sig) { · elektronisch am {{ sig.signedAt | hnDate }} · Prüfsumme <span class="hn-mono">{{ sig.bindingHash.slice(0, 16).toUpperCase() }}</span> }
          </p>
        </div>
        <div>
          <div class="h-24 border-b border-slate-400"></div>
          <p class="mt-1 text-xs text-slate-600">Ort, Datum, Unterschrift Käufer (Händler)</p>
        </div>
      </section>
    </article>
  `,
})
export class BelegPrintComponent {
  private readonly api = inject(AnkaufApi);
  readonly intake = input.required<Intake>();
  readonly dealer = input('');
  protected readonly declaration = OWNERSHIP_DECLARATION;
  protected readonly signatureUrl = signal<string | null>(null);

  constructor() {
    effect(() => {
      const i = this.intake();
      untracked(() => void this.load(i));
    });
    inject(DestroyRef).onDestroy(() => this.revoke());
  }

  private async load(i: Intake): Promise<void> {
    this.revoke();
    if (!i.signature?.hasImage) return;
    try {
      this.signatureUrl.set(URL.createObjectURL(await firstValueFrom(this.api.signatureImage(i.id, i.signature.id))));
    } catch {
      this.signatureUrl.set(null);
    }
  }

  private revoke(): void {
    const url = this.signatureUrl();
    if (url) URL.revokeObjectURL(url);
    this.signatureUrl.set(null);
  }
}
