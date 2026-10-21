import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AdminManagementApi } from '../../../core/api/admin-management-api';
import { fieldErrors, toApiProblem } from '../../../core/api/api-problem';
import { AdminApi } from '../../../core/api/handynet-api';
import {
  AdminMember,
  AdminOrganizationDetail,
  MemberRole,
  OrganizationProfileInput,
} from '../../../core/api/models';
import { AuthService } from '../../../core/auth/auth.service';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { AdminTabsComponent } from '../admin-tabs';
import {
  CAPABILITIES,
  MAX_REASON_LENGTH,
  MEMBER_ROLES,
  ProfileField,
  canChangeRole,
  isLastOwner,
  normalizeProfile,
  normalizeReason,
  profileChanged,
  profileOf,
  transferCandidates,
  validateProfile,
  validateReason,
} from './admin-management.logic';

interface PendingAction {
  title: string;
  text: string;
  confirmLabel: string;
  reasonRequired: boolean;
  danger: boolean;
  testId: string;
  /** Extra select of the transfer dialog. */
  transfer?: boolean;
  run: (reason: string | null) => Observable<unknown>;
  successKey: string;
  successParams?: Record<string, string>;
}

/**
 * Platform admin: one company — master data (editable, validated like the registration), status with history, modules,
 * members (role change, removal, ownership transfer; the last owner is protected), registers (names only) and counts.
 * Operational data of the tenant (receipts, customers, sales, Ankauf seller data) is not shown. Opening the page is
 * access-logged by the API; every change is audited with the reason.
 */
@Component({
  selector: 'hn-admin-company-detail-page',
  imports: [FormsModule, RouterLink, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './admin-company-detail.page.html',
})
export class AdminCompanyDetailPage {
  private readonly management = inject(AdminManagementApi);
  private readonly adminApi = inject(AdminApi);
  private readonly auth = inject(AuthService);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  readonly id = input.required<string>();

  protected readonly detail = createQuery(
    () => this.id(),
    (id) => this.management.company(id),
  );
  protected readonly allModules = createQuery(
    () => true,
    () => this.adminApi.modules(),
  );
  protected readonly org = computed(() => this.detail.value()?.organization ?? null);
  protected readonly capabilities = CAPABILITIES;
  protected readonly roles = MEMBER_ROLES;
  protected readonly maxReason = MAX_REASON_LENGTH;

  // ---------------------------------------------------------------- master data form
  protected form: OrganizationProfileInput | null = null;
  protected formReason = '';
  protected readonly submitted = signal(false);
  protected readonly saving = signal(false);
  protected readonly saveError = signal<unknown>(null);
  /** Bumped on every form change so computed() re-reads the (mutable) form. */
  protected readonly formTick = signal(0);
  protected readonly clientErrors = computed(() => {
    this.formTick();
    return this.form ? validateProfile(this.form) : {};
  });
  protected readonly dirty = computed(() => {
    this.formTick();
    const o = this.org();
    return !!o && !!this.form && profileChanged(this.form, profileOf(o));
  });

  // ---------------------------------------------------------------- members
  protected roleDraft: Record<string, MemberRole> = {};

  // ---------------------------------------------------------------- dialogs
  protected readonly pending = signal<PendingAction | null>(null);
  protected reason = '';
  protected transferTo = '';
  protected readonly reasonError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<unknown>(null);

  protected readonly modulesOpen = signal(false);
  protected selectedModules: Record<string, boolean> = {};

  constructor() {
    // Each loaded detail refreshes the member role pickers; the master-data form is refreshed unless it has unsaved edits
    // of the same company (a status action of this admin must not discard them).
    effect(() => {
      const d = this.detail.value();
      if (!d) return;
      untracked(() => {
        this.roleDraft = Object.fromEntries(d.members.map((m) => [m.userId, m.role]));
        if (!this.form || this.loadedId !== d.organization.id || !this.dirty()) this.resetForm(d);
      });
    });
  }

  private loadedId = '';

  private resetForm(d: AdminOrganizationDetail): void {
    this.form = { ...profileOf(d.organization) };
    this.loadedId = d.organization.id;
    this.submitted.set(false);
    this.saveError.set(null);
    this.formTick.update((n) => n + 1);
  }

  protected touched(): void {
    this.formTick.update((n) => n + 1);
  }

  protected fieldError(field: ProfileField): string | null {
    const client = this.clientErrors()[field];
    if (this.submitted() && client) return this.i18n.t(client);
    const server = this.saveError()
      ? fieldErrors(
          toApiProblem(this.saveError()),
          field === 'capabilities' ? 'capabilities' : field,
        )
      : [];
    return server.length ? server.join(' ') : null;
  }

  protected discard(): void {
    const d = this.detail.value();
    if (d) this.resetForm(d);
  }

  protected async save(): Promise<void> {
    this.submitted.set(true);
    const d = this.detail.value();
    if (!this.form || !d || Object.keys(this.clientErrors()).length || !this.dirty()) return;
    const reasonProblem = validateReason(this.formReason, false);
    if (reasonProblem) return;
    this.saving.set(true);
    this.saveError.set(null);
    try {
      const updated = await firstValueFrom(
        this.management.updateCompany(
          d.organization.id,
          normalizeProfile(this.form),
          d.version,
          normalizeReason(this.formReason),
        ),
      );
      this.detail.set(updated);
      this.formReason = '';
      this.resetForm(updated);
      this.toast.success(
        this.i18n.t('adminMgmt.company.saved', { name: updated.organization.name }),
      );
      void this.auth.loadMe(true);
    } catch (e) {
      this.saveError.set(e);
    } finally {
      this.saving.set(false);
    }
  }

  // ---------------------------------------------------------------- status
  protected askStatus(action: 'verify' | 'reject' | 'suspend' | 'reactivate'): void {
    const o = this.org();
    if (!o) return;
    this.ask({
      title: this.i18n.t(`adminMgmt.status.${action}Title`, { name: o.name }),
      text: this.i18n.t(`adminMgmt.status.${action}Text`),
      confirmLabel: this.i18n.t(`adminMgmt.status.${action}`),
      reasonRequired: action === 'reject' || action === 'suspend',
      danger: action === 'reject' || action === 'suspend',
      testId: `company-status-${action}`,
      run: (reason) => this.management.setStatus(o.id, action, reason),
      successKey: `adminMgmt.status.${action}Done`,
      successParams: { name: o.name },
    });
  }

  // ---------------------------------------------------------------- modules
  protected openModules(): void {
    const o = this.org();
    if (!o) return;
    this.actionError.set(null);
    this.selectedModules = Object.fromEntries(
      (this.allModules.value() ?? []).map((m) => [m, o.modules.includes(m)]),
    );
    this.modulesOpen.set(true);
  }

  protected async saveModules(): Promise<void> {
    const o = this.org();
    if (!o) return;
    const modules = Object.entries(this.selectedModules)
      .filter(([, on]) => on)
      .map(([m]) => m);
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await firstValueFrom(this.management.setModules(o.id, modules));
      this.toast.success(this.i18n.t('admin.modulesSaved', { name: o.name }));
      this.modulesOpen.set(false);
      this.detail.reload();
    } catch (e) {
      this.actionError.set(e);
    } finally {
      this.busy.set(false);
    }
  }

  // ---------------------------------------------------------------- members
  protected isLastOwner(m: AdminMember): boolean {
    return isLastOwner(this.detail.value()?.members ?? [], m);
  }

  protected canApplyRole(m: AdminMember): boolean {
    const role = this.roleDraft[m.userId];
    return !!role && canChangeRole(this.detail.value()?.members ?? [], m, role);
  }

  protected readonly transferCandidates = computed(() =>
    transferCandidates(this.detail.value()?.members ?? []),
  );

  protected askRole(m: AdminMember): void {
    const o = this.org();
    const role = this.roleDraft[m.userId];
    if (!o || !role || !this.canApplyRole(m)) return;
    const roleName = this.i18n.enum('role', role);
    this.ask({
      title: this.i18n.t('adminMgmt.members.roleTitle', { name: m.displayName }),
      text: this.i18n.t('adminMgmt.members.roleText', { name: m.displayName, role: roleName }),
      confirmLabel: this.i18n.t('adminMgmt.members.changeRole'),
      reasonRequired: false,
      danger: false,
      testId: 'member-role',
      run: (reason) => this.management.changeMemberRole(o.id, m.userId, role, reason),
      successKey: 'adminMgmt.members.roleChanged',
      successParams: { name: m.displayName, role: roleName },
    });
  }

  protected askRemove(m: AdminMember): void {
    const o = this.org();
    if (!o || this.isLastOwner(m)) return;
    this.ask({
      title: this.i18n.t('adminMgmt.members.removeTitle', { name: m.displayName }),
      text: this.i18n.t('adminMgmt.members.removeText', { name: m.displayName, company: o.name }),
      confirmLabel: this.i18n.t('adminMgmt.members.remove'),
      reasonRequired: true,
      danger: true,
      testId: 'member-remove',
      run: (reason) => this.management.removeMember(o.id, m.userId, reason ?? ''),
      successKey: 'adminMgmt.members.removed',
      successParams: { name: m.displayName },
    });
  }

  protected askTransfer(): void {
    const o = this.org();
    if (!o) return;
    this.transferTo = this.transferCandidates()[0]?.userId ?? '';
    this.ask({
      title: this.i18n.t('adminMgmt.members.transferTitle', { name: o.name }),
      text: this.i18n.t('adminMgmt.members.transferText'),
      confirmLabel: this.i18n.t('adminMgmt.members.transfer'),
      reasonRequired: false,
      danger: true,
      testId: 'member-transfer',
      transfer: true,
      run: (reason) => this.management.transferOwnership(o.id, this.transferTo, reason),
      successKey: 'adminMgmt.members.transferred',
      successParams: { name: o.name },
    });
  }

  // ---------------------------------------------------------------- dialog
  private ask(action: PendingAction): void {
    this.reason = '';
    this.reasonError.set(null);
    this.actionError.set(null);
    this.pending.set(action);
  }

  protected async confirm(): Promise<void> {
    const action = this.pending();
    if (!action) return;
    const problem = validateReason(this.reason, action.reasonRequired);
    this.reasonError.set(problem);
    if (problem || (action.transfer && !this.transferTo)) return;
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const result = await firstValueFrom(action.run(normalizeReason(this.reason)));
      this.toast.success(this.i18n.t(action.successKey, action.successParams));
      this.pending.set(null);
      // Member actions return the whole detail; status actions only the company row.
      const full = result as Partial<AdminOrganizationDetail> | null;
      if (full?.members) this.detail.set(full as AdminOrganizationDetail);
      else this.detail.reload();
      void this.auth.loadMe(true);
    } catch (e) {
      this.actionError.set(e);
    } finally {
      this.busy.set(false);
    }
  }
}
