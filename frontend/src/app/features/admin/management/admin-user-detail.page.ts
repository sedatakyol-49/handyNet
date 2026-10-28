import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { AdminManagementApi } from '../../../core/api/admin-management-api';
import { AdminUserAction, AdminUserDetail } from '../../../core/api/models';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { createQuery } from '../../../shared/data/query';
import { UI } from '../../../shared/ui';
import { AdminTabsComponent } from '../admin-tabs';
import {
  DANGER_ACTIONS,
  MAX_REASON_LENGTH,
  REASON_REQUIRED,
  availableUserActions,
  normalizeReason,
  userBadges,
  validateReason,
} from './admin-management.logic';

interface PendingUserAction {
  key: AdminUserAction | 'rename';
  title: string;
  text: string;
  reasonRequired: boolean;
  danger: boolean;
  /** Email actions: language of the email. */
  withLanguage: boolean;
  run: (reason: string | null) => Observable<AdminUserDetail>;
}

/**
 * Platform admin: one user account — profile, verification, last sign-in, lockout, disabled flag, active sessions and
 * memberships, with the account actions (each confirmed in a dialog, destructive ones with a required reason). The API
 * refuses self-disable, self-revoke and revoking the last platform admin. Never shows password hashes or tokens.
 */
@Component({
  selector: 'hn-admin-user-detail-page',
  imports: [FormsModule, RouterLink, AdminTabsComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="mb-2 text-sm">
      <a class="hn-link" routerLink="/app/administration/benutzer"
        >← {{ 'admin.tabs.users' | t }}</a
      >
    </nav>
    <hn-page-header
      [title]="user.value()?.displayName ?? ('adminMgmt.user.title' | t)"
      [description]="'adminMgmt.user.subtitle' | t"
    >
      @if (user.value(); as u) {
        @for (b of badges(u); track b.key) {
          <hn-status-badge [status]="b.key" kind="adminMgmt.userBadge" [tone]="b.tone" />
        }
      }
    </hn-page-header>
    <hn-admin-tabs />

    <hn-banner class="mb-3" [error]="user.error()" />

    @if (user.value(); as u) {
      <div class="grid gap-4 lg:grid-cols-3">
        <section
          class="hn-card p-4 lg:col-span-2"
          aria-labelledby="user-profile-heading"
          data-testid="user-profile"
        >
          <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
            <h2 id="user-profile-heading" class="text-sm">{{ 'adminMgmt.user.profile' | t }}</h2>
            <button
              hnButton
              size="sm"
              type="button"
              (click)="askRename(u)"
              data-testid="user-rename"
            >
              {{ 'adminMgmt.user.rename' | t }}
            </button>
          </div>
          <dl class="grid grid-cols-1 gap-x-4 gap-y-2 text-sm sm:grid-cols-[auto_1fr]">
            <dt class="hn-muted">{{ 'adminMgmt.user.displayName' | t }}</dt>
            <dd data-testid="user-display-name">
              <bdi>{{ u.displayName }}</bdi>
            </dd>
            <dt class="hn-muted">{{ 'adminMgmt.user.email' | t }}</dt>
            <dd class="break-all" dir="ltr">
              <bdi>{{ u.email }}</bdi>
            </dd>
            <dt class="hn-muted">{{ 'adminMgmt.users.verified' | t }}</dt>
            <dd data-testid="user-verified">
              @if (u.emailVerified) {
                {{ 'adminMgmt.yes' | t }}
                @if (u.emailVerifiedAt) {
                  · {{ u.emailVerifiedAt | hnDate }}
                }
              } @else {
                {{ 'adminMgmt.no' | t }}
                @if (u.verificationEmailRequestedAt) {
                  ·
                  {{
                    'adminMgmt.user.linkSent'
                      | t: { date: (u.verificationEmailRequestedAt | hnDate) }
                  }}
                }
              }
            </dd>
            <dt class="hn-muted">{{ 'adminMgmt.user.created' | t }}</dt>
            <dd>{{ u.createdAt | hnDate }}</dd>
            <dt class="hn-muted">{{ 'adminMgmt.users.lastLogin' | t }}</dt>
            <dd>{{ u.lastLoginAt ? (u.lastLoginAt | hnDate) : ('adminMgmt.users.never' | t) }}</dd>
            <dt class="hn-muted">{{ 'adminMgmt.user.failedLogins' | t }}</dt>
            <dd>
              {{ u.failedLoginCount }}
              @if (u.isLockedOut && u.lockedUntil) {
                ·
                <span class="text-amber-800">{{
                  'adminMgmt.user.lockedUntil' | t: { date: (u.lockedUntil | hnDate) }
                }}</span>
              }
            </dd>
            <dt class="hn-muted">{{ 'adminMgmt.user.sessions' | t }}</dt>
            <dd data-testid="user-sessions">{{ u.activeSessions }}</dd>
            @if (u.passwordResetRequestedAt) {
              <dt class="hn-muted">{{ 'adminMgmt.user.resetRequested' | t }}</dt>
              <dd>{{ u.passwordResetRequestedAt | hnDate }}</dd>
            }
            @if (u.isDisabled) {
              <dt class="hn-muted">{{ 'adminMgmt.users.disabled' | t }}</dt>
              <dd class="text-red-800" data-testid="user-disabled-reason">
                {{ u.disabledAt | hnDate }}
                @if (u.disabledReason) {
                  · <bdi>{{ u.disabledReason }}</bdi>
                }
              </dd>
            }
          </dl>
        </section>

        <section
          class="hn-card p-4"
          aria-labelledby="user-actions-heading"
          data-testid="user-actions"
        >
          <h2 id="user-actions-heading" class="mb-3 text-sm">{{ 'adminMgmt.user.actions' | t }}</h2>
          @if (u.isSelf) {
            <p class="mb-3 text-xs text-slate-600">{{ 'adminMgmt.user.selfHint' | t }}</p>
          }
          <div class="flex flex-col gap-2">
            @for (a of actions(); track a) {
              <button
                hnButton
                [variant]="danger.has(a) ? 'danger' : a === 'enable' ? 'primary' : 'secondary'"
                type="button"
                class="w-full justify-start"
                (click)="askAction(u, a)"
                [attr.data-testid]="'user-action-' + a"
              >
                {{ 'adminMgmt.user.action.' + a | t }}
              </button>
            } @empty {
              <p class="text-xs text-slate-500">{{ 'adminMgmt.user.noActions' | t }}</p>
            }
          </div>
        </section>
      </div>

      <section class="mt-4" aria-labelledby="user-memberships-heading">
        <h2 id="user-memberships-heading" class="mb-2 text-sm">
          {{ 'adminMgmt.users.organizations' | t }}
        </h2>
        <hn-table>
          <table class="hn-table hn-stack" data-testid="user-memberships">
            <thead>
              <tr>
                <th scope="col">{{ 'admin.organization' | t }}</th>
                <th scope="col">{{ 'team.role' | t }}</th>
                <th scope="col">{{ 'common.status' | t }}</th>
                <th scope="col">{{ 'team.since' | t }}</th>
              </tr>
            </thead>
            <tbody>
              @for (m of u.memberships; track m.organizationId) {
                <tr>
                  <td [attr.data-label]="'admin.organization' | t">
                    <a
                      class="hn-link font-medium"
                      [routerLink]="['/app/administration/unternehmen', m.organizationId]"
                      ><bdi>{{ m.organizationName }}</bdi></a
                    >
                  </td>
                  <td [attr.data-label]="'team.role' | t">{{ m.role | tEnum: 'role' }}</td>
                  <td [attr.data-label]="'common.status' | t"><hn-status-badge [status]="m.organizationStatus" kind="orgStatus" /></td>
                  <td [attr.data-label]="'team.since' | t" class="whitespace-nowrap text-xs">{{ m.joinedAt | hnDate }}</td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="4">
                    <hn-empty-state [title]="'adminMgmt.users.noOrganization' | t" />
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </hn-table>
      </section>
    } @else if (user.loading()) {
      <div class="hn-card p-4"><hn-skeleton-rows [rows]="6" /></div>
    }

    <hn-dialog
      [open]="!!pending()"
      [title]="pending()?.title ?? ''"
      (closed)="pending.set(null)"
      testId="user-dialog"
    >
      @if (pending(); as p) {
        <form class="space-y-4" (ngSubmit)="confirm()" novalidate>
          <p class="text-sm" data-testid="user-dialog-text">{{ p.text }}</p>
          @if (p.key === 'rename') {
            <hn-field
              [label]="'adminMgmt.user.displayName' | t"
              for="user-new-name"
              [required]="true"
              [errors]="nameError() ? (nameError()! | t) : null"
            >
              <input
                hnInput
                id="user-new-name"
                name="displayName"
                maxlength="200"
                required
                [(ngModel)]="displayName"
                [attr.aria-invalid]="!!nameError()"
                [attr.aria-describedby]="nameError() ? 'user-new-name-error' : null"
                data-testid="user-new-name"
              />
            </hn-field>
          }
          @if (p.withLanguage) {
            <hn-field [label]="'adminMgmt.user.emailLanguage' | t" for="user-email-lang">
              <select hnSelect id="user-email-lang" name="language" [(ngModel)]="language">
                <option value="de">Deutsch</option>
                <option value="en">English</option>
              </select>
            </hn-field>
          }
          <hn-field
            [label]="(p.reasonRequired ? 'orders.reason' : 'adminMgmt.reasonOptional') | t"
            for="user-reason"
            [required]="p.reasonRequired"
            [errors]="reasonError() ? (reasonError()! | t) : null"
          >
            <textarea
              hnInput
              id="user-reason"
              name="reason"
              rows="3"
              [maxlength]="maxReason"
              [required]="p.reasonRequired"
              [(ngModel)]="reason"
              [attr.aria-invalid]="!!reasonError()"
              [attr.aria-describedby]="reasonError() ? 'user-reason-error' : null"
              data-testid="user-reason"
            ></textarea>
          </hn-field>
          <hn-banner [error]="actionError()" />
          <div class="flex justify-end gap-2">
            <button hnButton type="button" (click)="pending.set(null)">
              {{ 'common.cancel' | t }}
            </button>
            <button
              hnButton
              [variant]="p.danger ? 'danger' : 'primary'"
              type="submit"
              [loading]="busy()"
              [disabled]="busy()"
              data-testid="user-confirm"
            >
              {{ (p.key === 'rename' ? 'common.save' : 'adminMgmt.user.action.' + p.key) | t }}
            </button>
          </div>
        </form>
      }
    </hn-dialog>
  `,
})
export class AdminUserDetailPage {
  private readonly api = inject(AdminManagementApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  readonly id = input.required<string>();

  protected readonly user = createQuery(
    () => this.id(),
    (id) => this.api.user(id),
  );
  protected readonly actions = computed(() => {
    const u = this.user.value();
    return u ? availableUserActions(u) : [];
  });
  protected readonly badges = userBadges;
  protected readonly danger = DANGER_ACTIONS;
  protected readonly maxReason = MAX_REASON_LENGTH;

  protected readonly pending = signal<PendingUserAction | null>(null);
  protected reason = '';
  protected displayName = '';
  protected language = 'de';
  protected readonly reasonError = signal<string | null>(null);
  protected readonly nameError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<unknown>(null);

  protected askAction(u: AdminUserDetail, action: AdminUserAction): void {
    this.open({
      key: action,
      title: this.i18n.t(`adminMgmt.user.action.${action}`),
      text: this.i18n.t(`adminMgmt.user.confirm.${action}`, {
        name: u.displayName,
        email: u.email,
      }),
      reasonRequired: REASON_REQUIRED.has(action),
      danger: DANGER_ACTIONS.has(action),
      withLanguage: action === 'password-reset' || action === 'resend-verification',
      run: (reason) => this.api.userAction(u.id, action, reason, this.language),
    });
  }

  protected askRename(u: AdminUserDetail): void {
    this.displayName = u.displayName;
    this.open({
      key: 'rename',
      title: this.i18n.t('adminMgmt.user.rename'),
      text: this.i18n.t('adminMgmt.user.renameText', { email: u.email }),
      reasonRequired: false,
      danger: false,
      withLanguage: false,
      run: (reason) => this.api.renameUser(u.id, this.displayName.trim(), reason),
    });
  }

  private open(action: PendingUserAction): void {
    this.reason = '';
    this.language = 'de';
    this.reasonError.set(null);
    this.nameError.set(null);
    this.actionError.set(null);
    this.pending.set(action);
  }

  protected async confirm(): Promise<void> {
    const p = this.pending();
    if (!p) return;
    const reasonProblem = validateReason(this.reason, p.reasonRequired);
    const nameProblem =
      p.key === 'rename' && !this.displayName.trim() ? 'validation.required' : null;
    this.reasonError.set(reasonProblem);
    this.nameError.set(nameProblem);
    if (reasonProblem || nameProblem) return;
    this.busy.set(true);
    this.actionError.set(null);
    try {
      const updated = await firstValueFrom(p.run(normalizeReason(this.reason)));
      this.user.set(updated);
      this.pending.set(null);
      this.toast.success(
        this.i18n.t(
          p.key === 'rename' ? 'adminMgmt.user.renamed' : `adminMgmt.user.done.${p.key}`,
          { name: updated.displayName, email: updated.email },
        ),
      );
    } catch (e) {
      this.actionError.set(e);
    } finally {
      this.busy.set(false);
    }
  }
}
