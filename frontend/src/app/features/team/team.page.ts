import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, fieldErrors, toApiProblem } from '../../core/api/api-problem';
import { OrganizationApi } from '../../core/api/handynet-api';
import { StaffRole } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { ToastService } from '../../core/ui-state/toast.service';
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
      <table class="hn-table">
        <thead>
          <tr>
            <th scope="col">{{ 'common.name' | t }}</th>
            <th scope="col">{{ 'auth.email' | t }}</th>
            <th scope="col">{{ 'team.role' | t }}</th>
            <th scope="col">{{ 'team.since' | t }}</th>
          </tr>
        </thead>
        <tbody>
          @for (s of staff.value() ?? []; track s.userId) {
            <tr>
              <td class="font-medium">{{ s.displayName }}</td>
              <td>{{ s.email }}</td>
              <td><hn-status-badge [status]="s.role" kind="role" tone="neutral" /></td>
              <td class="whitespace-nowrap">{{ s.createdAt | hnDate: 'date' }}</td>
            </tr>
          } @empty {
            @if (!staff.loading()) {
              <tr><td colspan="4"><hn-empty-state [title]="'team.empty' | t" /></td></tr>
            }
          }
        </tbody>
      </table>
    </hn-table>

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

  protected readonly staff = createQuery(() => true, () => this.api.staff());
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
