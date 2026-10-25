import { ChangeDetectionStrategy, Component, DOCUMENT, DestroyRef, computed, inject, output, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AnkaufApi } from '../../core/api/handynet-api';
import { CheckIn, CheckInCreated } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { toApiProblem } from '../../core/api/api-problem';
import { UI } from '../../shared/ui';
import { QrCodeComponent } from '../pos/qr-code';

/** Seconds between status checks while the seller fills in the form. */
export const CHECKIN_POLL_MS = 2000;

/** mm:ss until the link expires (0:00 when expired). */
export function countdown(expiresAt: string, now: number): string {
  const s = Math.max(0, Math.floor((new Date(expiresAt).getTime() - now) / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

/**
 * QR self check-in at the counter: shows a QR code with a short-lived single-use link; the seller fills in their data
 * (and may sign) on their own phone; the data appears here for the employee to compare with the ID document.
 */
@Component({
  selector: 'hn-checkin-panel',
  imports: [QrCodeComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @if (!created()) {
      <div class="flex flex-col items-center gap-3 rounded-xl border border-dashed border-slate-300 px-4 py-6 text-center">
        <span class="grid size-12 place-items-center rounded-2xl bg-accent-50 text-2xl text-accent-800" aria-hidden="true">⌗</span>
        <p class="max-w-md text-sm text-slate-600">{{ 'ankauf.checkin.intro' | t }}</p>
        <button hnButton variant="primary" type="button" [loading]="busy()" [disabled]="busy()" (click)="start()" data-testid="checkin-start">
          {{ 'ankauf.checkin.start' | t }}
        </button>
        <hn-banner [text]="error()" />
      </div>
    } @else if (checkIn(); as c) {
      @if (c.status === 'Open') {
        <div class="grid items-center gap-4 sm:grid-cols-[auto_minmax(0,1fr)]" data-testid="checkin-open">
          <div class="mx-auto rounded-2xl border border-slate-200 bg-white p-3 shadow-sm">
            <hn-qr-code [data]="url()" [sizePx]="176" [label]="'ankauf.checkin.qrLabel' | t" />
          </div>
          <div class="space-y-2 text-sm">
            <p class="font-semibold">{{ 'ankauf.checkin.scan' | t }}</p>
            <p class="text-slate-600">{{ 'ankauf.checkin.scanHint' | t }}</p>
            <p class="flex items-center gap-2 text-xs text-slate-600">
              <span class="inline-block size-2 animate-pulse rounded-full bg-accent-600" aria-hidden="true"></span>
              <span role="status">{{ 'ankauf.checkin.waiting' | t }}</span>
              <span class="ms-auto rounded-full bg-slate-100 px-2 py-0.5 font-medium tabular-nums" data-testid="checkin-countdown">{{ remaining() }}</span>
            </p>
            <details class="text-xs">
              <summary class="cursor-pointer text-slate-600 hover:text-slate-900">{{ 'ankauf.checkin.showLink' | t }}</summary>
              <p class="mt-1 break-all hn-mono" dir="ltr" data-testid="checkin-url">{{ url() }}</p>
            </details>
            <button hnButton size="sm" variant="ghost" type="button" (click)="cancel()">{{ 'common.cancel' | t }}</button>
          </div>
        </div>
      } @else if (c.status === 'Submitted' && c.seller) {
        <div class="space-y-3" data-testid="checkin-submitted">
          <hn-banner tone="success" [heading]="'ankauf.checkin.submittedHeading' | t" [text]="'ankauf.checkin.verify' | t" />
          <dl class="grid gap-x-4 gap-y-1 rounded-xl border border-slate-200 bg-white p-3 text-sm sm:grid-cols-2">
            <div><dt class="text-xs text-slate-500">{{ 'common.name' | t }}</dt><dd class="font-medium" data-testid="checkin-name">{{ c.seller.firstName }} {{ c.seller.lastName }}</dd></div>
            <div><dt class="text-xs text-slate-500">{{ 'ankauf.intake.birthDate' | t }}</dt><dd>{{ c.seller.birthDate | hnDate: 'date' }}</dd></div>
            <div class="sm:col-span-2"><dt class="text-xs text-slate-500">{{ 'ankauf.seller.address' | t }}</dt><dd>{{ c.seller.street }}, {{ c.seller.postalCode }} {{ c.seller.city }}</dd></div>
            <div><dt class="text-xs text-slate-500">{{ 'ankauf.intake.idDocumentType' | t }}</dt><dd>{{ c.seller.idDocumentType | tEnum: 'idDocumentType' }}</dd></div>
            <div><dt class="text-xs text-slate-500">{{ 'ankauf.intake.idDocumentNumber' | t }}</dt><dd class="hn-mono">{{ c.seller.idDocumentNumber }}</dd></div>
            <div><dt class="text-xs text-slate-500">{{ 'ankauf.seller.expiry' | t }}</dt><dd>{{ c.seller.idExpiryDate ? (c.seller.idExpiryDate | hnDate: 'date') : '–' }}</dd></div>
            @if (c.hasSignature && signatureUrl()) {
              <div class="sm:col-span-2">
                <dt class="text-xs text-slate-500">{{ 'ankauf.checkin.phoneSignature' | t }}</dt>
                <dd><img [src]="signatureUrl()" alt="" class="mt-1 h-16 rounded border border-slate-200 bg-white" data-testid="checkin-signature" /></dd>
              </div>
            }
          </dl>
          <div class="flex flex-wrap justify-end gap-2">
            <button hnButton type="button" (click)="cancel()">{{ 'ankauf.checkin.discard' | t }}</button>
            <button hnButton variant="primary" type="button" (click)="take(c)" data-testid="checkin-take">{{ 'ankauf.checkin.take' | t }}</button>
          </div>
        </div>
      } @else {
        <div class="space-y-2 text-sm" data-testid="checkin-ended">
          <hn-banner tone="warning" [text]="(c.status === 'Consumed' ? 'ankauf.checkin.used' : 'ankauf.checkin.expired') | t" />
          <button hnButton size="sm" type="button" (click)="start()">{{ 'ankauf.checkin.again' | t }}</button>
        </div>
      }
    }
  `,
})
export class CheckInPanelComponent {
  private readonly api = inject(AnkaufApi);
  private readonly i18n = inject(I18nService);
  private readonly document = inject(DOCUMENT);
  readonly taken = output<CheckIn>();

  protected readonly created = signal<CheckInCreated | null>(null);
  protected readonly checkIn = signal<CheckIn | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly signatureUrl = signal<string | null>(null);
  private readonly now = signal(Date.now());
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly url = computed(() => {
    const c = this.created();
    return c ? `${this.document.location?.origin ?? ''}${c.path}` : '';
  });
  protected readonly remaining = computed(() => {
    const c = this.created();
    return c ? countdown(c.expiresAt, this.now()) : '';
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.stop());
  }

  protected async start(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    this.stop();
    try {
      const c = await firstValueFrom(this.api.createCheckIn());
      this.created.set(c);
      this.checkIn.set({ id: c.id, status: 'Open', createdAt: '', expiresAt: c.expiresAt, submittedAt: null, seller: null, hasSignature: false, signedAt: null, intakeId: null });
      this.timer = setInterval(() => void this.poll(), CHECKIN_POLL_MS);
    } catch (e) {
      this.error.set(describeProblem(this.i18n, toApiProblem(e)));
    } finally {
      this.busy.set(false);
    }
  }

  private async poll(): Promise<void> {
    const c = this.created();
    this.now.set(Date.now());
    if (!c) return;
    try {
      const current = await firstValueFrom(this.api.checkIn(c.id));
      this.checkIn.set(current);
      if (current.status !== 'Open') {
        this.stop();
        if (current.status === 'Submitted' && current.hasSignature) {
          const blob = await firstValueFrom(this.api.checkInSignature(c.id));
          this.signatureUrl.set(URL.createObjectURL(blob));
        }
      }
    } catch {
      /* transient; the next poll retries */
    }
  }

  protected take(c: CheckIn): void {
    this.taken.emit(c);
  }

  protected cancel(): void {
    this.stop();
    this.created.set(null);
    this.checkIn.set(null);
  }

  private stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    const url = this.signatureUrl();
    if (url) URL.revokeObjectURL(url);
    this.signatureUrl.set(null);
  }
}
