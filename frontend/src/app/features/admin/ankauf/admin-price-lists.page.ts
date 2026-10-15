import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../../core/api/api-problem';
import { AdminAnkaufApi } from '../../../core/api/handynet-api';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { ankaufProblemText } from '../../ankauf/ankauf.logic';
import { AdminTabsComponent } from '../admin-tabs';
import { AnkaufAdminNavComponent } from './ankauf-admin-nav';
import { browserTimeZone, defaultValidity, validityToIso } from './ankauf-admin.logic';

const MAX_BYTES = 10 * 1024 * 1024;

/** Platform admin: upload monthly Ankauf price lists (XLSX) and list all lists with their review state. */
@Component({
  selector: 'hn-admin-price-lists-page',
  imports: [FormsModule, RouterLink, AdminTabsComponent, AnkaufAdminNavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'admin.title' | t" [description]="'ankaufAdmin.lists.subtitle' | t">
      <button hnButton type="button" [loading]="lists.loading()" (click)="lists.reload()">{{ 'common.refresh' | t }}</button>
    </hn-page-header>
    <hn-admin-tabs />
    <hn-ankauf-admin-nav />

    <section class="hn-card mb-4 p-4" aria-labelledby="pl-upload-heading">
      <h2 id="pl-upload-heading" class="mb-1 text-sm">{{ 'ankaufAdmin.upload.title' | t }}</h2>
      <p class="mb-3 text-xs text-slate-600">{{ 'ankaufAdmin.upload.rules' | t }}</p>
      <form class="space-y-3" (ngSubmit)="upload()" novalidate>
        <hn-banner [text]="uploadErrorText()" />
        <div class="grid gap-3 md:grid-cols-2 lg:grid-cols-3">
          <hn-field [label]="'ankaufAdmin.upload.file' | t" for="pl-file" [required]="true" [hint]="'ankaufAdmin.upload.fileHint' | t" [errors]="errors()['file'] ? (errors()['file'] | t) : null">
            <input id="pl-file" type="file" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
              class="block w-full text-sm file:me-3 file:rounded-md file:border file:border-slate-300 file:bg-white file:px-3 file:py-1.5 file:text-sm file:font-medium hover:file:bg-slate-50"
              (change)="onFile($event)" [attr.aria-invalid]="!!errors()['file']" [attr.aria-describedby]="errors()['file'] ? 'pl-file-error' : 'pl-file-hint'" data-testid="pl-file" />
          </hn-field>
          <hn-field [label]="'ankaufAdmin.upload.label' | t" for="pl-label" [required]="true" [hint]="'ankaufAdmin.upload.labelHint' | t" [errors]="errors()['label'] ? (errors()['label'] | t) : null">
            <input hnInput id="pl-label" name="label" maxlength="100" [(ngModel)]="label" [attr.aria-invalid]="!!errors()['label']" data-testid="pl-label" />
          </hn-field>
          <hn-field [label]="'ankaufAdmin.upload.sheet' | t" for="pl-sheet" [hint]="'ankaufAdmin.upload.sheetHint' | t">
            <input hnInput id="pl-sheet" name="sheet" maxlength="100" [(ngModel)]="sheet" placeholder="Mobilfunk" data-testid="pl-sheet" />
          </hn-field>
        </div>
        <fieldset class="grid gap-3 sm:grid-cols-3">
          <legend class="mb-1 text-xs font-medium text-slate-700">{{ 'ankaufAdmin.upload.validity' | t }}</legend>
          <hn-field [label]="'ankaufAdmin.upload.validFrom' | t" for="pl-from" [required]="true" [errors]="errors()['dates'] ? (errors()['dates'] | t) : null">
            <input hnInput id="pl-from" name="from" type="date" required [(ngModel)]="fromDate" [attr.aria-invalid]="!!errors()['dates']" data-testid="pl-from" />
          </hn-field>
          <hn-field [label]="'ankaufAdmin.upload.validFromTime' | t" for="pl-from-time">
            <input hnInput id="pl-from-time" name="fromTime" type="time" [(ngModel)]="fromTime" data-testid="pl-from-time" />
          </hn-field>
          <hn-field [label]="'ankaufAdmin.upload.validUntil' | t" for="pl-until" [required]="true" [hint]="'ankaufAdmin.upload.validUntilHint' | t">
            <input hnInput id="pl-until" name="until" type="date" required [(ngModel)]="untilDate" data-testid="pl-until" />
          </hn-field>
        </fieldset>
        <p class="text-xs text-slate-600" data-testid="pl-timezone">{{ 'ankaufAdmin.upload.timezone' | t: { zone: timeZone } }}</p>
        <div class="flex justify-end">
          <button hnButton variant="primary" type="submit" [loading]="uploading()" [disabled]="uploading()" data-testid="pl-upload">
            {{ 'ankaufAdmin.upload.submit' | t }}
          </button>
        </div>
      </form>
    </section>

    <hn-banner class="mb-3" [error]="lists.error()" />
    <hn-table [loading]="lists.loading()">
      <table class="hn-table" data-testid="pl-table">
        <thead>
          <tr>
            <th scope="col">{{ 'ankaufAdmin.lists.label' | t }}</th>
            <th scope="col">{{ 'ankaufAdmin.lists.validity' | t }}</th>
            <th scope="col">{{ 'common.status' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankaufAdmin.summary.total' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankaufAdmin.summary.pending' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankaufAdmin.summary.approved' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankaufAdmin.summary.rejected' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankaufAdmin.summary.autoAccepted' | t }}</th>
            <th scope="col" class="text-end">{{ 'ankaufAdmin.summary.skipped' | t }}</th>
            <th scope="col">{{ 'ankaufAdmin.lists.importedAt' | t }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (l of lists.value() ?? []; track l.id) {
            <tr [attr.data-list]="l.label">
              <td>
                <span class="font-medium">{{ l.label }}</span>
                <span class="block text-xs text-slate-500">{{ l.fileName }} · {{ l.sheetName }}</span>
              </td>
              <td class="whitespace-nowrap text-xs">{{ l.validFrom | hnDate }}<br />– {{ l.validUntil | hnDate }}</td>
              <td><hn-status-badge [status]="l.status" kind="priceListStatus" [tone]="statusTone(l.status)" /></td>
              <td class="text-end tabular-nums">{{ l.summary.total }}</td>
              <td class="text-end tabular-nums" [class.text-amber-700]="l.summary.pending > 0" [class.font-semibold]="l.summary.pending > 0">{{ l.summary.pending }}</td>
              <td class="text-end tabular-nums">{{ l.summary.approved }}</td>
              <td class="text-end tabular-nums">{{ l.summary.rejected }}</td>
              <td class="text-end tabular-nums">{{ l.summary.autoAccepted }}</td>
              <td class="text-end tabular-nums">{{ l.summary.skipped }}</td>
              <td class="whitespace-nowrap text-xs">{{ l.importedAt | hnDate }}</td>
              <td class="text-end">
                <a hnButton size="sm" [variant]="l.status === 'Review' ? 'primary' : 'secondary'" [routerLink]="['/app/administration/ankauf/preislisten', l.id]"
                  [attr.aria-label]="'ankaufAdmin.lists.openLabel' | t: { label: l.label }" data-testid="pl-open">
                  {{ (l.status === 'Review' ? 'ankaufAdmin.lists.review' : 'ankaufAdmin.lists.open') | t }}
                </a>
              </td>
            </tr>
          } @empty {
            @if (!lists.loading()) {
              <tr><td colspan="11"><hn-empty-state [title]="'ankaufAdmin.lists.empty' | t" [text]="'ankaufAdmin.lists.emptyText' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
    </hn-table>
  `,
})
export class AdminPriceListsPage {
  private readonly api = inject(AdminAnkaufApi);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  protected readonly lists = createQuery(
    () => true,
    () => this.api.lists(),
  );

  protected readonly timeZone = browserTimeZone();
  private readonly defaults = defaultValidity(new Date());
  protected fromDate = this.defaults.from;
  protected fromTime = '00:00';
  protected untilDate = this.defaults.until;
  protected label = '';
  protected sheet = '';
  private file: File | null = null;

  protected readonly uploading = signal(false);
  protected readonly errors = signal<Record<string, string>>({});
  private readonly uploadError = signal<unknown>(null);
  protected readonly uploadErrorText = computed(() => {
    const e = this.uploadError();
    if (!e) return null;
    return ankaufProblemText(toApiProblem(e), (k) => this.i18n.t(k), (k) => this.i18n.has(k), (p) => describeProblem(this.i18n, p));
  });

  protected statusTone(status: string): 'warning' | 'success' | 'neutral' {
    return status === 'Review' ? 'warning' : status === 'Published' ? 'success' : 'neutral';
  }

  protected onFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.file = input.files?.[0] ?? null;
    const { file: _, ...rest } = this.errors();
    this.errors.set(rest);
  }

  protected async upload(): Promise<void> {
    const errors: Record<string, string> = {};
    if (!this.file) errors['file'] = 'ankaufAdmin.upload.fileRequired';
    else if (!this.file.name.toLowerCase().endsWith('.xlsx')) errors['file'] = 'errors.codes.ankauf.format';
    else if (this.file.size > MAX_BYTES) errors['file'] = 'errors.codes.ankauf.too_large';
    if (!this.label.trim()) errors['label'] = 'ankaufAdmin.upload.labelRequired';
    const validity = validityToIso(this.fromDate, this.fromTime, this.untilDate);
    if (validity.error) errors['dates'] = validity.error;
    this.errors.set(errors);
    this.uploadError.set(null);
    if (Object.keys(errors).length) return;

    this.uploading.set(true);
    try {
      const list = await firstValueFrom(
        this.api.upload({ file: this.file!, label: this.label.trim(), validFrom: validity.validFrom!, validUntil: validity.validUntil!, sheet: this.sheet.trim() || null }),
      );
      this.toast.success(this.i18n.t('ankaufAdmin.upload.done', { label: list.label }));
      await this.router.navigate(['/app/administration/ankauf/preislisten', list.id]);
    } catch (e) {
      this.uploadError.set(e);
    } finally {
      this.uploading.set(false);
    }
  }
}
