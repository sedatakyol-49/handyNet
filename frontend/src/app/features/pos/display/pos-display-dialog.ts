import { ChangeDetectionStrategy, Component, PLATFORM_ID, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { firstValueFrom } from 'rxjs';
import { I18nService } from '../../../core/i18n/i18n.service';
import { UI } from '../../../shared/ui';
import { posErrorText } from '../pos-error';
import { QrCodeComponent } from '../qr-code';
import { DisplayApi, DisplayLink, DisplayLinkStatus } from './display-api';
import { DisplayPublisher } from './display-publisher';

/** Path of the local display window of a register. */
export function localDisplayPath(registerId: string): string {
  return `/kasse/${registerId}/kundendisplay`;
}

/** Full URL of a remote display link for the QR code (the API returns a path of the web app). */
export function displayLinkUrl(origin: string, path: string): string {
  return origin.replace(/\/+$/, '') + (path.startsWith('/') ? path : '/' + path);
}

/**
 * Till menu → "Kundendisplay": open the local display window (second screen of this computer) and manage the link for a
 * remote display (tablet or another device: QR code, revoke). The link token is shown only right after it was created.
 */
@Component({
  selector: 'hn-pos-display-dialog',
  imports: [QrCodeComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="'pos.display.menu' | t" [open]="open()" size="lg" testId="pos-display-dialog" (closed)="closed.emit()">
      @if (open()) {
        <div class="grid gap-4 md:grid-cols-2">
          <section class="pos-card p-4" aria-labelledby="pos-display-local-heading">
            <h3 id="pos-display-local-heading" class="text-base font-bold">{{ 'pos.display.localTitle' | t }}</h3>
            <p class="mt-1 text-sm text-pos-muted">{{ 'pos.display.localText' | t }}</p>
            <button type="button" class="pos-soft pos-soft-accent mt-3 w-full" (click)="openLocal()" data-testid="pos-display-open-local">{{ 'pos.display.openLocal' | t }}</button>
            @if (popupBlocked()) {
              <p class="mt-2 text-sm text-pos-danger" role="alert" data-testid="pos-display-popup-blocked">
                {{ 'pos.display.popupBlocked' | t }}
                <a class="font-semibold underline" [href]="localPath()" target="_blank" rel="noopener">{{ 'pos.display.openInTab' | t }}</a>
              </p>
            }
          </section>

          <section class="pos-card p-4" aria-labelledby="pos-display-remote-heading">
            <h3 id="pos-display-remote-heading" class="text-base font-bold">{{ 'pos.display.remoteTitle' | t }}</h3>
            <p class="mt-1 text-sm text-pos-muted">{{ 'pos.display.remoteText' | t }}</p>
            <hn-banner class="mt-2" [text]="error()" />
            @if (link(); as l) {
              <div class="mt-3 flex flex-wrap items-center gap-3" data-testid="pos-display-qr">
                <hn-qr-code [data]="url()!" [label]="'pos.display.qrLabel' | t" [sizePx]="148" />
                <div class="min-w-0 flex-1 text-xs text-pos-muted">
                  <p class="font-semibold text-pos-ink">{{ 'pos.display.qrHint' | t }}</p>
                  <p class="mt-1 break-all" dir="ltr" data-testid="pos-display-url">{{ url() }}</p>
                  <p class="mt-1">{{ 'pos.display.validUntil' | t: { date: (l.expiresAt | hnDate: 'date') } }}</p>
                  <p class="mt-1">{{ 'pos.display.tokenOnce' | t }}</p>
                </div>
              </div>
            } @else if (status()?.active) {
              <p class="mt-3 rounded-[10px] bg-pos-ok-soft px-3 py-2 text-sm text-pos-ok-text" role="status" data-testid="pos-display-active">
                {{ 'pos.display.active' | t: { date: (status()!.expiresAt | hnDate: 'date') } }}
              </p>
            } @else if (statusKnown()) {
              <p class="mt-3 text-sm text-pos-muted" data-testid="pos-display-none">{{ 'pos.display.none' | t }}</p>
            }
            @if (pushError()) {
              <p class="mt-2 rounded-[10px] bg-pos-warn-soft px-3 py-2 text-sm text-pos-ink" role="status" data-testid="pos-display-push-error">{{ 'pos.display.pushFailed' | t }}</p>
            }
            <div class="mt-3 flex flex-wrap gap-2">
              <button type="button" class="pos-soft pos-soft-accent" [disabled]="busy()" [attr.aria-busy]="busy() || null" (click)="createLink()" data-testid="pos-display-create">
                {{ (status()?.active || link() ? 'pos.display.recreate' : 'pos.display.create') | t }}
              </button>
              @if (status()?.active || link()) {
                <button type="button" class="pos-soft text-pos-danger" [disabled]="busy()" (click)="revoke()" data-testid="pos-display-revoke">{{ 'pos.display.revoke' | t }}</button>
              }
            </div>
            @if (status()?.active || link()) {
              <p class="mt-2 text-xs text-pos-muted">{{ 'pos.display.recreateHint' | t }}</p>
            }
          </section>
        </div>
        <p class="mt-3 text-xs text-pos-muted">{{ 'pos.display.privacy' | t }}</p>
      }
    </hn-dialog>
  `,
})
export class PosDisplayDialogComponent {
  readonly registerId = input.required<string>();
  readonly open = input(false);
  readonly closed = output<void>();

  private readonly api = inject(DisplayApi);
  private readonly publisher = inject(DisplayPublisher);
  private readonly i18n = inject(I18nService);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  protected readonly status = signal<DisplayLinkStatus | null>(null);
  protected readonly statusKnown = signal(false);
  protected readonly link = signal<DisplayLink | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly popupBlocked = signal(false);
  protected readonly pushError = computed(() => !!this.publisher.remoteError() && (!!this.status()?.active || !!this.link()));
  protected readonly localPath = computed(() => localDisplayPath(this.registerId()));
  protected readonly url = computed(() => {
    const l = this.link();
    return l && this.browser ? displayLinkUrl(window.location.origin, l.path) : null;
  });

  constructor() {
    effect(() => {
      const open = this.open();
      const id = this.registerId();
      if (open) untracked(() => void this.loadStatus(id));
      else
        untracked(() => {
          this.link.set(null); // the token is not kept after the dialog closes
          this.error.set(null);
          this.popupBlocked.set(false);
        });
    });
  }

  private async loadStatus(registerId: string): Promise<void> {
    this.statusKnown.set(false);
    try {
      const s = await firstValueFrom(this.api.linkStatus(registerId));
      this.status.set(s);
      this.publisher.setRemote(registerId, !!s?.active);
    } catch (e) {
      this.status.set(null);
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.statusKnown.set(true);
    }
  }

  protected openLocal(): void {
    if (!this.browser) return;
    const w = window.open(this.localPath(), `hn-display-${this.registerId()}`, 'popup=yes,width=1024,height=768');
    this.popupBlocked.set(!w);
  }

  protected async createLink(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      const l = await firstValueFrom(this.api.createLink(this.registerId()));
      this.link.set(l);
      this.status.set({ active: true, expiresAt: l.expiresAt });
      this.publisher.setRemote(this.registerId(), true);
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }

  protected async revoke(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.api.revokeLink(this.registerId()));
      this.link.set(null);
      this.status.set(null);
      this.publisher.setRemote(this.registerId(), false);
    } catch (e) {
      this.error.set(posErrorText(this.i18n, e));
    } finally {
      this.busy.set(false);
    }
  }
}
