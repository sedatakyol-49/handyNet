import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { SessionStore } from '../../../core/auth/session.store';
import { I18nService } from '../../../core/i18n/i18n.service';
import { ToastService } from '../../../core/ui-state/toast.service';
import { UI } from '../../../shared/ui';
import { posErrorText } from '../pos-error';
import { PosSubnavComponent } from '../pos-subnav';
import { DEVICE_MANUAL, cardLabel, effectiveTerminal } from './card-flow';
import { CardTransaction, TERMINAL_CONNECTIONS, TerminalApi, TerminalProfile } from './terminal-api';
import { TERMINAL_LIMITS, TerminalForm, TerminalFormErrors, emptyTerminalForm, formFromTerminal, terminalInput } from './terminal-form';
import { TerminalService } from './terminal.service';
import { TerminalOutcome, terminalSummary } from './terminal-transports';

type Status = { tone: 'ok' | 'danger'; text: string };
type OrphanAction = { kind: 'terminal' | 'manual'; transaction: CardTransaction };

/**
 * Kasse → Kartenterminal: card terminal profiles of one register (modelled on the printer page). Everyone who sells
 * chooses what this device uses and tests the connection; profiles are created, changed and deleted with `pos.manage`.
 * Managers also see "Kartenzahlungen ohne Beleg": approved card payments that no receipt uses, to reverse them.
 */
@Component({
  selector: 'hn-pos-terminals-page',
  host: { class: 'pos-page block' },
  imports: [FormsModule, PosSubnavComponent, ...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-pos-subnav [registerId]="registerId()" />
    <div class="mb-4 flex flex-wrap items-end justify-between gap-3">
      <div class="min-w-0">
        <h1 class="text-2xl font-extrabold tracking-tight">{{ 'pos.terminals.title' | t }}</h1>
        <p class="mt-1 max-w-3xl text-sm text-pos-muted">{{ 'pos.terminals.subtitle' | t }}</p>
      </div>
      @if (canManage()) {
        <button type="button" class="pos-soft pos-soft-accent" (click)="openCreate()" data-testid="pos-terminal-new">{{ 'pos.terminals.new' | t }}</button>
      }
    </div>

    <hn-banner class="mb-3" [text]="loadError()" />
    <hn-banner class="mb-3" tone="warning" [heading]="'pos.terminals.unverifiedTitle' | t" [text]="'pos.terminals.unverified' | t" data-testid="pos-terminal-unverified" />

    <section class="pos-card mb-4 p-4" aria-labelledby="pos-terminal-device-heading" data-testid="pos-terminal-device">
      <h2 id="pos-terminal-device-heading" class="pos-section-label">{{ 'pos.terminals.device.title' | t }}</h2>
      <div class="mt-2 flex flex-wrap items-end gap-3">
        <hn-field class="min-w-64 flex-1" [label]="'pos.terminals.device.choice' | t" for="pos-terminal-choice" [hint]="'pos.terminals.device.hint' | t">
          <select hnSelect id="pos-terminal-choice" class="min-h-11" [ngModel]="choice()" (ngModelChange)="setChoice($event)" aria-describedby="pos-terminal-choice-hint" data-testid="pos-terminal-choice">
            <option value="">{{ 'pos.terminals.device.registerDefault' | t: { name: defaultName() } }}</option>
            <option [value]="manualChoice">{{ 'pos.terminals.device.manual' | t }}</option>
            @for (p of profiles(); track p.id) {
              <option [value]="p.id">{{ p.name }}</option>
            }
          </select>
        </hn-field>
        <p class="pb-2 text-sm" data-testid="pos-terminal-effective">
          {{ 'pos.terminals.device.effective' | t }}: <strong>{{ effective()?.name ?? ('pos.terminals.device.manual' | t) }}</strong>
        </p>
      </div>
    </section>

    @if (loaded() && profiles().length === 0) {
      <div class="pos-card mb-4">
        <hn-empty-state [title]="'pos.terminals.emptyTitle' | t" [text]="(canManage() ? 'pos.terminals.emptyManage' : 'pos.terminals.emptySell') | t" />
      </div>
    }

    <ul class="grid gap-4 lg:grid-cols-2" data-testid="pos-terminals">
      @for (p of profiles(); track p.id) {
        @let st = status()[p.id];
        <li class="pos-card flex flex-col p-4" [attr.data-terminal]="p.name">
          <div class="flex items-start justify-between gap-3">
            <div class="min-w-0">
              <h2 class="truncate text-lg font-bold">{{ p.name }}</h2>
              <p class="text-sm text-pos-muted">{{ 'pos.terminals.connection.' + p.connection | t }}</p>
            </div>
            <div class="flex flex-wrap justify-end gap-1.5">
              @if (p.connection === 'Simulated') {
                <span class="pos-pill bg-pos-warn-soft text-pos-ink" data-testid="pos-terminal-test-badge">{{ 'pos.terminals.testOnly' | t }}</span>
              }
              @if (p.isDefault) {
                <span class="pos-pill bg-pos-accent-soft text-pos-accent-text" data-testid="pos-terminal-default">{{ 'pos.terminals.default' | t }}</span>
              }
              @if (effective()?.id === p.id) {
                <span class="pos-pill bg-pos-ok-soft text-pos-ok-text">{{ 'pos.terminals.thisDevice' | t }}</span>
              }
            </div>
          </div>
          <p class="mt-2 text-sm text-pos-muted">{{ 'pos.terminals.help.' + p.connection | t }}</p>
          @if (summary(p); as s) {
            <p class="mt-2 truncate font-mono text-xs text-pos-muted" dir="ltr" [attr.title]="s" data-testid="pos-terminal-address">{{ s }}</p>
          }
          @if (st) {
            <p class="mt-3 rounded-[10px] px-3 py-2 text-sm" [class]="st.tone === 'ok' ? 'bg-pos-ok-soft text-pos-ok-text' : 'bg-pos-danger-soft text-pos-ink'" role="status" data-testid="pos-terminal-status">
              {{ st.text }}
            </p>
          }
          <div class="mt-auto flex flex-wrap gap-2 pt-3">
            @if (p.connection === 'ZvtBridge') {
              <button type="button" class="pos-soft pos-soft-accent" [disabled]="busyId() === p.id" [attr.aria-busy]="busyId() === p.id || null" (click)="test(p)" data-testid="pos-terminal-test">
                {{ 'pos.terminals.test' | t }}
              </button>
              @if (canManage()) {
                <button type="button" class="pos-soft" [disabled]="busyId() === p.id" (click)="endOfDayFor.set(p)" data-testid="pos-terminal-eod">{{ 'pos.terminals.endOfDay' | t }}</button>
              }
            }
            @if (canManage()) {
              <button type="button" class="pos-soft" (click)="openEdit(p)" data-testid="pos-terminal-edit">{{ 'common.edit' | t }}</button>
              @if (!p.isDefault) {
                <button type="button" class="pos-soft" (click)="makeDefault(p)" data-testid="pos-terminal-make-default">{{ 'pos.terminals.makeDefault' | t }}</button>
              }
              <button type="button" class="pos-soft text-pos-danger" (click)="deleting.set(p)" data-testid="pos-terminal-delete">{{ 'pos.terminals.delete' | t }}</button>
            }
          </div>
        </li>
      }
    </ul>

    @if (canManage()) {
      <section class="pos-card mt-6 p-4" aria-labelledby="pos-orphans-heading" data-testid="pos-card-orphans">
        <div class="flex flex-wrap items-start justify-between gap-3">
          <div class="min-w-0">
            <h2 id="pos-orphans-heading" class="text-lg font-bold">{{ 'pos.terminals.orphans.title' | t }}</h2>
            <p class="mt-1 max-w-3xl text-sm text-pos-muted">{{ 'pos.terminals.orphans.text' | t }}</p>
          </div>
          <button type="button" class="pos-soft" (click)="loadOrphans()" [attr.aria-busy]="orphansLoading() || null">{{ 'common.refresh' | t }}</button>
        </div>
        <hn-banner class="mt-3" [text]="orphansError()" />
        @if (orphansLoaded() && !orphans().length) {
          <p class="mt-3 text-sm text-pos-muted" data-testid="pos-card-orphans-empty">{{ 'pos.terminals.orphans.empty' | t }}</p>
        }
        @if (orphans().length) {
          <ul class="mt-3 divide-y divide-pos-line" data-testid="pos-card-orphans-list">
            @for (t of orphans(); track t.id) {
              <li class="flex flex-wrap items-center gap-x-4 gap-y-2 py-3" data-testid="pos-card-orphan">
                <div class="min-w-0 flex-1">
                  <p class="font-semibold">
                    <bdi dir="ltr" class="tabular-nums" data-testid="pos-card-orphan-amount">{{ t.amountMinor | money }}</bdi>
                    <span class="ms-2 font-normal text-pos-muted">{{ label(t) }}</span>
                  </p>
                  <p class="text-xs text-pos-muted">
                    {{ t.createdAt | hnDate: 'datetime' }}
                    @if (t.createdByName) {
                      · {{ t.createdByName }}
                    }
                    @if (t.traceNumber) {
                      · {{ 'pos.terminals.orphans.trace' | t: { trace: t.traceNumber } }}
                    }
                    @if (t.terminalReceiptNumber) {
                      · {{ 'pos.terminals.orphans.receipt' | t: { number: t.terminalReceiptNumber } }}
                    }
                  </p>
                </div>
                <div class="flex flex-wrap gap-2">
                  <button type="button" class="pos-soft pos-soft-accent" (click)="orphanAction.set({ kind: 'terminal', transaction: t })" data-testid="pos-card-orphan-reverse">
                    {{ 'pos.terminals.orphans.reverse' | t }}
                  </button>
                  <button type="button" class="pos-soft" (click)="orphanAction.set({ kind: 'manual', transaction: t })" data-testid="pos-card-orphan-manual">
                    {{ 'pos.terminals.orphans.markReversed' | t }}
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </section>
    }

    <hn-dialog [title]="(editing() ? 'pos.terminals.editTitle' : 'pos.terminals.newTitle') | t" [open]="formOpen()" size="lg" testId="pos-terminal-form" (closed)="closeForm()">
      @if (formOpen()) {
        <hn-banner class="mb-3" [text]="formError()" />
        <form class="grid gap-3 sm:grid-cols-2" (ngSubmit)="save()" novalidate>
          <hn-field class="sm:col-span-2" [label]="'common.name' | t" for="tf-name" [required]="true" [errors]="err('name')">
            <input hnInput id="tf-name" name="name" autocomplete="off" [maxlength]="limits.name" [(ngModel)]="form.name" data-testid="tf-name" />
          </hn-field>
          <hn-field class="sm:col-span-2" [label]="'pos.terminals.form.connection' | t" for="tf-connection">
            <select hnSelect id="tf-connection" name="connection" class="min-h-11" [(ngModel)]="form.connection" data-testid="tf-connection">
              @for (c of connections; track c) {
                <option [value]="c">{{ 'pos.terminals.connection.' + c | t }}</option>
              }
            </select>
          </hn-field>
          <p class="rounded-[10px] bg-pos-surface px-3 py-2 text-sm text-pos-muted sm:col-span-2" data-testid="tf-help">{{ 'pos.terminals.help.' + form.connection | t }}</p>

          @if (form.connection === 'ZvtBridge') {
            <hn-field [label]="'pos.terminals.form.host' | t" for="tf-host" [required]="true" [hint]="'pos.terminals.form.hostHint' | t" [errors]="err('host')">
              <input hnInput id="tf-host" name="host" dir="ltr" autocomplete="off" inputmode="url" [maxlength]="limits.host" [(ngModel)]="form.host" aria-describedby="tf-host-hint" data-testid="tf-host" />
            </hn-field>
            <hn-field [label]="'pos.terminals.form.port' | t" for="tf-port" [hint]="'pos.terminals.form.portHint' | t" [errors]="err('port')">
              <input hnInput id="tf-port" name="port" dir="ltr" inputmode="numeric" autocomplete="off" class="w-32" [(ngModel)]="form.port" aria-describedby="tf-port-hint" data-testid="tf-port" />
            </hn-field>
            <hn-field class="sm:col-span-2" [label]="'pos.terminals.form.bridgeUrl' | t" for="tf-bridge" [required]="true" [hint]="'pos.terminals.form.bridgeUrlHint' | t" [errors]="err('bridgeUrl')">
              <input hnInput id="tf-bridge" name="bridge" dir="ltr" autocomplete="off" inputmode="url" [maxlength]="limits.bridgeUrl" [(ngModel)]="form.bridgeUrl" aria-describedby="tf-bridge-hint" data-testid="tf-bridge" />
            </hn-field>
            <hn-field [label]="'pos.terminals.form.password' | t" for="tf-password" [hint]="'pos.terminals.form.passwordHint' | t" [errors]="err('zvtPassword')">
              <input hnInput id="tf-password" name="password" dir="ltr" inputmode="numeric" autocomplete="off" maxlength="6" class="w-32 font-mono" [(ngModel)]="form.zvtPassword" aria-describedby="tf-password-hint" data-testid="tf-password" />
            </hn-field>
          }
          @if (!editing()?.isDefault) {
            <label class="flex min-h-11 items-start gap-2 text-sm sm:col-span-2">
              <input type="checkbox" class="hn-checkbox mt-1" name="isDefault" [(ngModel)]="form.isDefault" data-testid="tf-default" />
              <span class="font-medium">{{ 'pos.terminals.form.isDefault' | t }}</span>
            </label>
          }
          <div class="flex justify-end gap-2 sm:col-span-2">
            <button hnButton class="min-h-11" type="button" (click)="closeForm()">{{ 'common.cancel' | t }}</button>
            <button hnButton class="min-h-11" variant="primary" type="submit" [loading]="saving()" [disabled]="saving()" data-testid="tf-save">{{ 'common.save' | t }}</button>
          </div>
        </form>
      }
    </hn-dialog>

    <hn-dialog [title]="'pos.terminals.deleteTitle' | t" [open]="!!deleting()" (closed)="deleting.set(null)">
      @if (deleting(); as p) {
        <p class="text-sm">{{ 'pos.terminals.deleteText' | t: { name: p.name } }}</p>
        <div class="mt-4 flex justify-end gap-2">
          <button hnButton class="min-h-11" type="button" (click)="deleting.set(null)">{{ 'common.cancel' | t }}</button>
          <button hnButton class="min-h-11" variant="danger" type="button" [loading]="saving()" [disabled]="saving()" (click)="remove(p)" data-testid="pos-terminal-delete-confirm">{{ 'pos.terminals.delete' | t }}</button>
        </div>
      }
    </hn-dialog>

    <hn-dialog [title]="'pos.terminals.endOfDay' | t" [open]="!!endOfDayFor()" (closed)="endOfDayFor.set(null)">
      @if (endOfDayFor(); as p) {
        <p class="text-sm">{{ 'pos.terminals.endOfDayText' | t: { name: p.name } }}</p>
        <div class="mt-4 flex justify-end gap-2">
          <button hnButton class="min-h-11" type="button" (click)="endOfDayFor.set(null)">{{ 'common.cancel' | t }}</button>
          <button hnButton class="min-h-11" variant="primary" type="button" (click)="endOfDay(p)" data-testid="pos-terminal-eod-confirm">{{ 'pos.terminals.endOfDay' | t }}</button>
        </div>
      }
    </hn-dialog>

    <hn-dialog [title]="(orphanAction()?.kind === 'manual' ? 'pos.terminals.orphans.markReversed' : 'pos.terminals.orphans.reverse') | t" [open]="!!orphanAction()" (closed)="closeOrphan()">
      @if (orphanAction(); as a) {
        <p class="text-sm">
          {{ (a.kind === 'manual' ? 'pos.terminals.orphans.manualText' : 'pos.terminals.orphans.reverseText') | t: { amount: (a.transaction.amountMinor | money), card: label(a.transaction) } }}
        </p>
        @if (a.kind === 'manual') {
          <hn-field class="mt-3 block" [label]="'pos.terminals.orphans.manualNote' | t" for="pos-orphan-note" [required]="true" [errors]="orphanNoteError()">
            <input hnInput id="pos-orphan-note" name="note" autocomplete="off" maxlength="200" [(ngModel)]="orphanNote" data-testid="pos-card-orphan-note" />
          </hn-field>
        }
        <hn-banner class="mt-3" [text]="orphanError()" />
        <div class="mt-4 flex justify-end gap-2">
          <button hnButton class="min-h-11" type="button" (click)="closeOrphan()">{{ 'common.cancel' | t }}</button>
          <button hnButton class="min-h-11" variant="danger" type="button" [loading]="saving()" [disabled]="saving()" (click)="confirmOrphan(a)" data-testid="pos-card-orphan-confirm">
            {{ (a.kind === 'manual' ? 'pos.terminals.orphans.markReversed' : 'pos.terminals.orphans.reverse') | t }}
          </button>
        </div>
      }
    </hn-dialog>
  `,
})
export class PosTerminalsPage {
  readonly registerId = input.required<string>();

  private readonly api = inject(TerminalApi);
  private readonly terminals = inject(TerminalService);
  private readonly session = inject(SessionStore);
  private readonly i18n = inject(I18nService);
  private readonly toast = inject(ToastService);

  protected readonly limits = TERMINAL_LIMITS;
  protected readonly connections = TERMINAL_CONNECTIONS;
  protected readonly manualChoice = DEVICE_MANUAL;
  protected readonly canManage = computed(() => this.session.can('pos.manage'));

  protected readonly profiles = signal<TerminalProfile[]>([]);
  protected readonly loaded = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly choice = signal('');
  protected readonly effective = computed(() => effectiveTerminal(this.profiles(), this.choice()));
  protected readonly defaultName = computed(() => this.profiles().find((p) => p.isDefault)?.name ?? this.i18n.t('pos.terminals.device.manual'));
  protected readonly status = signal<Record<string, Status>>({});
  protected readonly busyId = signal<string | null>(null);

  protected readonly formOpen = signal(false);
  protected readonly editing = signal<TerminalProfile | null>(null);
  protected form: TerminalForm = emptyTerminalForm();
  protected readonly formErrors = signal<TerminalFormErrors>({});
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly deleting = signal<TerminalProfile | null>(null);
  protected readonly endOfDayFor = signal<TerminalProfile | null>(null);

  protected readonly orphans = signal<CardTransaction[]>([]);
  protected readonly orphansLoaded = signal(false);
  protected readonly orphansLoading = signal(false);
  protected readonly orphansError = signal<string | null>(null);
  protected readonly orphanAction = signal<OrphanAction | null>(null);
  protected readonly orphanError = signal<string | null>(null);
  protected readonly orphanNoteError = signal<string | null>(null);
  protected orphanNote = '';

  ngOnInit(): void {
    this.choice.set(this.terminals.deviceChoice(this.registerId()));
    void this.load();
    if (this.canManage()) void this.loadOrphans();
  }

  protected summary(p: TerminalProfile): string {
    return terminalSummary(p);
  }

  protected label(t: CardTransaction): string {
    return cardLabel(t);
  }

  protected err(field: keyof TerminalForm): string | null {
    const key = this.formErrors()[field];
    return key ? this.i18n.t(key) : null;
  }

  private async load(): Promise<void> {
    this.loadError.set(null);
    try {
      const list = await firstValueFrom(this.api.list(this.registerId()));
      this.profiles.set(list);
      this.terminals.setProfiles(this.registerId(), list);
    } catch (e) {
      this.loadError.set(posErrorText(this.i18n, e));
    } finally {
      this.loaded.set(true);
    }
  }

  protected async loadOrphans(): Promise<void> {
    this.orphansLoading.set(true);
    this.orphansError.set(null);
    try {
      const page = await firstValueFrom(this.api.transactions({ registerId: this.registerId(), status: 'Approved', unlinked: true, page: 1, pageSize: 50 }));
      this.orphans.set(page.items);
    } catch (e) {
      this.orphansError.set(posErrorText(this.i18n, e));
    } finally {
      this.orphansLoading.set(false);
      this.orphansLoaded.set(true);
    }
  }

  protected setChoice(value: string): void {
    this.choice.set(value);
    this.terminals.setDeviceChoice(this.registerId(), value);
    this.toast.success(this.i18n.t('pos.terminals.device.saved'));
  }

  private setStatus(p: TerminalProfile, outcome: TerminalOutcome, okKey: string): void {
    const ok = outcome.status === 'Approved';
    const text = ok ? this.i18n.t(okKey, { name: p.name }) : this.terminals.outcomeText(outcome);
    this.status.update((s) => ({ ...s, [p.id]: { tone: ok ? 'ok' : 'danger', text } }));
  }

  protected async test(p: TerminalProfile): Promise<void> {
    this.busyId.set(p.id);
    try {
      this.setStatus(p, await this.terminals.test(p), 'pos.terminals.testOk');
    } finally {
      this.busyId.set(null);
    }
  }

  protected async endOfDay(p: TerminalProfile): Promise<void> {
    this.endOfDayFor.set(null);
    this.busyId.set(p.id);
    try {
      this.setStatus(p, await this.terminals.endOfDay(p), 'pos.terminals.endOfDayOk');
    } finally {
      this.busyId.set(null);
    }
  }

  protected openCreate(): void {
    this.form = emptyTerminalForm();
    this.form.isDefault = this.profiles().length === 0;
    this.editing.set(null);
    this.formErrors.set({});
    this.formError.set(null);
    this.formOpen.set(true);
  }

  protected openEdit(p: TerminalProfile): void {
    this.form = formFromTerminal(p);
    this.editing.set(p);
    this.formErrors.set({});
    this.formError.set(null);
    this.formOpen.set(true);
  }

  protected closeForm(): void {
    if (!this.saving()) this.formOpen.set(false);
  }

  protected async save(): Promise<void> {
    const editing = this.editing();
    const { input, errors } = terminalInput(this.form, editing?.version ?? null);
    this.formErrors.set(errors);
    if (!input) return;
    this.saving.set(true);
    this.formError.set(null);
    try {
      await firstValueFrom(editing ? this.api.update(editing.id, input) : this.api.create(this.registerId(), input));
      this.toast.success(this.i18n.t('pos.terminals.saved', { name: input.name }));
      this.formOpen.set(false);
      await this.load();
    } catch (e) {
      this.formError.set(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }

  protected async makeDefault(p: TerminalProfile): Promise<void> {
    try {
      await firstValueFrom(this.api.setDefault(p.id));
      this.toast.success(this.i18n.t('pos.terminals.defaultSet', { name: p.name }));
      await this.load();
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e));
    }
  }

  protected async remove(p: TerminalProfile): Promise<void> {
    this.saving.set(true);
    try {
      await firstValueFrom(this.api.delete(p.id));
      if (this.choice() === p.id) this.setChoice('');
      this.toast.success(this.i18n.t('pos.terminals.deleted', { name: p.name }));
      this.deleting.set(null);
      await this.load();
    } catch (e) {
      this.toast.error(posErrorText(this.i18n, e));
    } finally {
      this.saving.set(false);
    }
  }

  protected closeOrphan(): void {
    if (this.saving()) return;
    this.orphanAction.set(null);
    this.orphanError.set(null);
    this.orphanNoteError.set(null);
    this.orphanNote = '';
  }

  /** Reverses an approved payment without a receipt on the terminal, or records that it was reversed by hand there. */
  protected async confirmOrphan(a: OrphanAction): Promise<void> {
    const t = a.transaction;
    this.orphanError.set(null);
    if (a.kind === 'manual') {
      const note = this.orphanNote.trim();
      this.orphanNoteError.set(note ? null : this.i18n.t('pos.terminals.orphans.manualNoteRequired'));
      if (!note) return;
      this.saving.set(true);
      const r = await this.terminals.recordReversal(t.id, `${this.i18n.t('pos.terminals.orphans.manualPrefix')}: ${note}`);
      this.saving.set(false);
      if (!r.ok) return this.orphanError.set(r.text);
    } else {
      this.orphanAction.set(null); // the terminal dialog takes over
      const r = await this.terminals.reverse(this.registerId(), { transactionId: t.id, profileId: t.terminalProfileId, terminalReceiptNumber: t.terminalReceiptNumber ?? null, amountMinor: t.amountMinor });
      if (r.kind === 'failed' || r.kind === 'reversedUnreported') {
        this.toast.error(r.text);
        return void this.loadOrphans();
      }
      if (r.kind === 'notReversible') {
        this.toast.error(this.i18n.t('pos.terminals.flow.notReversible'));
        return void this.loadOrphans();
      }
    }
    this.orphanAction.set(null);
    this.orphanNote = '';
    this.toast.success(this.i18n.t('pos.terminals.orphans.reversed', { amount: this.money(t.amountMinor) }));
    await this.loadOrphans();
  }

  private money(minor: number): string {
    return new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' }).format(minor / 100);
  }
}
