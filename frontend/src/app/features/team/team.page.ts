import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom, of } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { OrganizationApi, PosApi } from '../../core/api/handynet-api';
import { POS_CONFIGURABLE_PERMISSIONS, PosConfigurablePermission, PosPinStatus, StaffMember, StaffRole } from '../../core/api/models';
import { SessionStore } from '../../core/auth/session.store';
import { PosRightsForm, effectivePosRights, posRightsConfigurable, posRightsKey, rightsFormFromStaff, rightsRequestFromForm } from '../pos/pos-rights';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService, describeProblem } from '../../core/ui-state/toast.service';
import { createQuery } from '../../shared/data/query';
import { UI } from '../../shared/ui';

@Component({
  selector: 'hn-team-page',
  imports: [FormsModule, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-page-header [title]="'team.title' | t" [description]="'team.subtitle' | t">
      <button hnButton variant="primary" type="button" (click)="openAdd()">{{ 'team.add' | t }}</button>
    </hn-page-header>

    <hn-banner class="mb-3" [error]="staff.error()" />

    <hn-table [loading]="staff.loading()">
      <table class="hn-table hn-stack">
        <thead>
          <tr>
            <th scope="col">{{ 'common.name' | t }}</th>
            <th scope="col">{{ 'auth.email' | t }}</th>
            <th scope="col">{{ 'team.role' | t }}</th>
            <th scope="col">{{ 'team.since' | t }}</th>
            @if (hasPos()) {
              <th scope="col">{{ 'team.posRights.column' | t }}</th>
              <th scope="col" class="hn-actions"><span class="sr-only">{{ 'common.actions' | t }}</span></th>
            }
          </tr>
        </thead>
        <tbody>
          @for (s of staff.value() ?? []; track s.userId) {
            <tr>
              <td [attr.data-label]="'common.name' | t" class="font-medium">{{ s.displayName }}</td>
              <td [attr.data-label]="'auth.email' | t">{{ s.email }}</td>
              <td [attr.data-label]="'team.role' | t"><hn-status-badge [status]="s.role" kind="role" tone="neutral" /></td>
              <td [attr.data-label]="'team.since' | t" class="whitespace-nowrap">{{ s.createdAt | hnDate: 'date' }}</td>
              @if (hasPos()) {
                <td [attr.data-label]="'team.posRights.column' | t" class="text-xs" [attr.data-staff]="s.email" data-testid="team-pos-rights">
                  @if (!configurable(s.role)) {
                    <span class="text-slate-600">{{ 'team.posRights.all' | t }}</span>
                  } @else {
                    <span class="flex flex-wrap gap-1">
                      @for (p of effective(s); track p) {
                        <span class="rounded bg-slate-100 px-1.5 py-0.5 text-slate-700">{{ 'team.posRights.short.' + key(p) | t }}</span>
                      } @empty {
                        <span class="text-slate-500">{{ 'team.posRights.none' | t }}</span>
                      }
                      @if (s.posPermissions === null || s.posPermissions === undefined) {
                        <span class="text-slate-500">({{ 'team.posRights.defaults' | t }})</span>
                      }
                    </span>
                    <span class="mt-0.5 block text-slate-600">{{ 'team.posRights.maxDiscountShort' | t: { percent: s.maxDiscountPercent ?? 100 } }}</span>
                  }
                  @let pin = pinOf(s.userId);
                  <span class="mt-0.5 block" [class]="pin?.lockedUntil ? 'font-semibold text-red-700' : 'text-slate-600'" data-testid="team-pin-status">
                    {{ (pin?.lockedUntil ? 'team.pin.locked' : pin?.hasPin ? 'team.pin.set' : 'team.pin.none') | t }}
                  </span>
                </td>
                <td class="hn-actions">
                  @if (configurable(s.role)) {
                    <button hnButton type="button" (click)="openRights(s)" [attr.aria-label]="'team.posRights.editFor' | t: { name: s.displayName }" data-testid="team-pos-edit">{{ 'team.posRights.edit' | t }}</button>
                  }
                  <button hnButton type="button" class="ms-1" (click)="openPin(s)" [attr.aria-label]="'team.pin.editFor' | t: { name: s.displayName }" data-testid="team-pin-edit">{{ 'team.pin.edit' | t }}</button>
                </td>
              }
            </tr>
          } @empty {
            @if (!staff.loading()) {
              <tr><td [attr.colspan]="hasPos() ? 6 : 4"><hn-empty-state [title]="'team.empty' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
    </hn-table>

    <hn-dialog [open]="!!rightsFor()" [title]="'team.posRights.title' | t: { name: rightsFor()?.displayName ?? '' }" (closed)="rightsFor.set(null)">
      @if (rightsFor(); as member) {
        <form class="space-y-4" (submit)="$event.preventDefault(); saveRights(member)" novalidate data-testid="team-pos-form">
          <p class="text-sm text-slate-600">{{ 'team.posRights.text' | t }}</p>
          <label class="flex min-h-11 items-center gap-2 text-sm">
            <input type="checkbox" class="hn-checkbox" [checked]="rightsForm().useDefaults" (change)="setDefaults($any($event.target).checked)" data-testid="team-pos-defaults" />
            {{ 'team.posRights.useDefaults' | t }}
          </label>
          <fieldset [disabled]="rightsForm().useDefaults" class="space-y-1 disabled:opacity-60">
            <legend class="mb-1 text-xs font-medium text-slate-700">{{ 'team.posRights.rights' | t }}</legend>
            @for (p of posPermissions; track p) {
              <label class="flex min-h-11 items-start gap-2 text-sm">
                <input type="checkbox" class="hn-checkbox mt-1" [checked]="rightsForm().granted[p]" (change)="toggleRight(p, $any($event.target).checked)" [attr.data-testid]="'team-pos-' + key(p)" />
                <span>
                  <span class="font-medium">{{ 'team.posRights.names.' + key(p) | t }}</span>
                  <span class="block text-xs text-slate-500">{{ 'team.posRights.help.' + key(p) | t }}</span>
                </span>
              </label>
            }
          </fieldset>
          <hn-field [label]="'team.posRights.maxDiscount' | t" for="team-pos-max" [hint]="'team.posRights.maxDiscountHint' | t" [errors]="rightsError()">
            <input hnInput id="team-pos-max" name="maxDiscount" inputmode="numeric" class="w-32 text-end" autocomplete="off" [value]="rightsForm().maxDiscount"
              (input)="setMax($any($event.target).value)" [attr.aria-invalid]="!!rightsError()" aria-describedby="team-pos-max-hint" data-testid="team-pos-max" />
          </hn-field>
          <div class="flex justify-end gap-2">
            <button hnButton type="button" (click)="rightsFor.set(null)">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="team-pos-save">{{ 'common.save' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>

    <hn-dialog [open]="!!pinFor()" [title]="'team.pin.title' | t: { name: pinFor()?.displayName ?? '' }" (closed)="pinFor.set(null)">
      @if (pinFor(); as member) {
        <form class="space-y-4" (submit)="$event.preventDefault(); savePin(member, pinInput())" novalidate data-testid="team-pin-form">
          <p class="text-sm text-slate-600">{{ 'team.pin.text' | t }}</p>
          <hn-field [label]="'team.pin.pin' | t" for="team-pin" [hint]="'team.pin.hint' | t" [errors]="pinError()">
            <input hnInput id="team-pin" name="pin" type="password" inputmode="numeric" maxlength="6" autocomplete="off" class="w-40 tracking-widest"
              [value]="pinInput()" (input)="pinInput.set($any($event.target).value); pinError.set(null)" [attr.aria-invalid]="!!pinError()"
              aria-describedby="team-pin-hint" data-testid="team-pin-input" />
          </hn-field>
          <div class="flex flex-wrap justify-end gap-2">
            @if (pinOf(member.userId)?.hasPin) {
              <button hnButton variant="ghost" type="button" class="me-auto" [disabled]="busy()" (click)="savePin(member, null)" data-testid="team-pin-clear">{{ 'team.pin.clear' | t }}</button>
            }
            <button hnButton type="button" (click)="pinFor.set(null)">{{ 'common.cancel' | t }}</button>
            <button hnButton variant="primary" type="submit" [loading]="busy()" [disabled]="busy()" data-testid="team-pin-save">{{ 'common.save' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>

    <hn-dialog [open]="open()" [title]="'team.add' | t" (closed)="open.set(false)">
      <form class="space-y-4" (ngSubmit)="submit()">
        <hn-banner [error]="problem()" />
        <hn-field [label]="'common.name' | t" for="staff-name" [required]="true" [errors]="errors('displayName')">
          <input hnInput id="staff-name" name="displayName" required [(ngModel)]="form.displayName" autocomplete="off" />
        </hn-field>
        <hn-field [label]="'auth.email' | t" for="staff-email" [required]="true" [errors]="errors('email')">
          <input hnInput id="staff-email" name="email" type="email" required [(ngModel)]="form.email" autocomplete="off" />
        </hn-field>
        <hn-field [label]="'team.initialPassword' | t" for="staff-password" [hint]="'team.passwordHint' | t" [errors]="errors('password')">
          <input hnInput id="staff-password" name="password" type="password" [(ngModel)]="form.password" autocomplete="new-password"
            aria-describedby="staff-password-hint" />
        </hn-field>
        <hn-field [label]="'team.role' | t" for="staff-role" [errors]="errors('role')">
          <select hnSelect id="staff-role" name="role" [(ngModel)]="form.role">
            <option value="Staff">{{ 'Staff' | tEnum: 'role' }}</option>
            <option value="Manager">{{ 'Manager' | tEnum: 'role' }}</option>
          </select>
        </hn-field>
        <div class="flex justify-end gap-2">
          <button hnButton type="button" (click)="open.set(false)">{{ 'common.cancel' | t }}</button>
          <button hnButton variant="primary" type="submit" [loading]="busy()" [disabled]="busy()">{{ 'team.add' | t }}</button>
        </div>
      </form>
    </hn-dialog>
  `,
})
export class TeamPage {
  private readonly api = inject(OrganizationApi);
  private readonly toast = inject(ToastService);
  private readonly i18n = inject(I18nService);

  private readonly session = inject(SessionStore);
  protected readonly staff = createQuery(() => true, () => this.api.staff());
  /** Per-cashier POS rights are shown and edited only when the organization has the POS module. */
  protected readonly hasPos = computed(() => this.session.hasModule('Pos'));
  protected readonly posPermissions = POS_CONFIGURABLE_PERMISSIONS;
  protected readonly rightsFor = signal<StaffMember | null>(null);
  protected readonly rightsForm = signal<PosRightsForm>(rightsFormFromStaff({ posPermissions: null, posMaxDiscountPercent: null }));
  protected readonly rightsError = signal<string | null>(null);
  protected readonly configurable = posRightsConfigurable;
  protected readonly effective = effectivePosRights;
  protected readonly key = posRightsKey;

  // ---- POS PIN (cashier switching / approvals on a shared device)
  private readonly pos = inject(PosApi);
  protected readonly pins = createQuery(() => this.hasPos(), (pos) => (pos ? this.pos.pins() : of([])));
  protected readonly pinFor = signal<StaffMember | null>(null);
  protected readonly pinInput = signal('');
  protected readonly pinError = signal<string | null>(null);

  protected pinOf(userId: string): PosPinStatus | undefined {
    return this.pins.value()?.find((p) => p.userId === userId);
  }

  protected openPin(s: StaffMember): void {
    this.pinInput.set('');
    this.pinError.set(null);
    this.pinFor.set(s);
  }

  protected async savePin(member: StaffMember, pin: string | null): Promise<void> {
    if (pin !== null && !/^[0-9]{4,6}$/.test(pin)) {
      this.pinError.set(this.i18n.t('team.pin.invalid'));
      return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(this.pos.setPin(member.userId, pin));
      this.toast.success(this.i18n.t(pin === null ? 'team.pin.cleared' : 'team.pin.saved', { name: member.displayName }));
      this.pinFor.set(null);
      this.pins.reload();
    } catch (e) {
      this.pinError.set(describeProblem(this.i18n, toApiProblem(e)));
    } finally {
      this.busy.set(false);
    }
  }

  protected openRights(s: StaffMember): void {
    this.rightsForm.set(rightsFormFromStaff(s));
    this.rightsError.set(null);
    this.rightsFor.set(s);
  }

  protected setDefaults(on: boolean): void {
    this.rightsForm.update((f) => ({ ...f, useDefaults: on }));
  }

  protected toggleRight(p: PosConfigurablePermission, on: boolean): void {
    this.rightsForm.update((f) => ({ ...f, granted: { ...f.granted, [p]: on } }));
  }

  protected setMax(value: string): void {
    this.rightsForm.update((f) => ({ ...f, maxDiscount: value }));
    this.rightsError.set(null);
  }

  protected async saveRights(member: StaffMember): Promise<void> {
    const r = rightsRequestFromForm(this.rightsForm());
    if (!r.ok) {
      this.rightsError.set(this.i18n.t('team.posRights.maxDiscountInvalid'));
      return;
    }
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.setPosRights(member.userId, r.body));
      this.toast.success(this.i18n.t('team.posRights.saved', { name: member.displayName }));
      this.rightsFor.set(null);
      this.staff.reload();
    } catch (e) {
      const problem = toApiProblem(e);
      this.rightsError.set(problem.code === 'staff.pos_rights_role' ? this.i18n.t('team.posRights.roleError') : describeProblem(this.i18n, problem));
    } finally {
      this.busy.set(false);
    }
  }
  protected readonly open = signal(false);
  protected readonly busy = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected form: { email: string; displayName: string; password: string; role: StaffRole } = this.empty();

  protected openAdd(): void {
    this.form = this.empty();
    this.problem.set(null);
    this.open.set(true);
  }

  protected errors(field: string): string[] {
    return fieldErrors(this.problem(), field);
  }

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.problem.set(null);
    try {
      await firstValueFrom(
        this.api.addStaff({
          email: this.form.email.trim(),
          displayName: this.form.displayName.trim(),
          password: this.form.password || null,
          role: this.form.role,
        }),
      );
      this.toast.success(this.i18n.t('team.added'));
      this.open.set(false);
      this.staff.reload();
    } catch (e) {
      this.problem.set(toApiProblem(e));
    } finally {
      this.busy.set(false);
    }
  }

  private empty(): { email: string; displayName: string; password: string; role: StaffRole } {
    return { email: '', displayName: '', password: '', role: 'Staff' };
  }
}
