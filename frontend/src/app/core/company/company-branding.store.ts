import { Injectable, computed, inject } from '@angular/core';
import { catchError, of } from 'rxjs';
import { OrganizationApi } from '../api/handynet-api';
import { CompanyProfile, ReceiptBranding } from '../api/models';
import { SessionStore } from '../auth/session.store';
import { createQuery } from '../../shared/data/query';

/**
 * The selected organization's company profile, loaded once per organization and shared by the shell (logo in the top
 * bar), the POS (top bar, receipt), the dashboard and the company page. Any member may read it; a failed load simply
 * means "no branding" (the UI falls back to the SEHA mark).
 */
@Injectable({ providedIn: 'root' })
export class CompanyBrandingStore {
  private readonly api = inject(OrganizationApi);
  private readonly session = inject(SessionStore);

  private readonly query = createQuery(
    () => (this.session.isAuthenticated() ? this.session.organizationId() : null),
    (organizationId) => (organizationId ? this.api.profile().pipe(catchError(() => of(null))) : of(null)),
  );

  /** Profile of the currently selected organization (never the previous one while switching). */
  readonly profile = computed<CompanyProfile | null>(() => {
    const p = this.query.value();
    return p && p.id === this.session.organizationId() ? p : null;
  });

  readonly loading = this.query.loading;

  /** Absolute URL of the screen logo, or null. */
  readonly logoUrl = computed(() => this.api.assetUrl(this.profile()?.logo?.screenUrl));

  /** Receipt branding of the own company (for receipts shown inside the app); logo URL relative like the API returns it. */
  readonly receiptBranding = computed<ReceiptBranding | null>(() => {
    const p = this.profile();
    return p ? { logoUrl: p.logo?.screenUrl ?? null, receiptHeader: p.receiptHeader, receiptFooter: p.receiptFooter } : null;
  });

  /** After a save or logo change on the company page. */
  set(profile: CompanyProfile): void {
    this.query.set(profile);
  }

  reload(): void {
    this.query.reload();
  }
}
