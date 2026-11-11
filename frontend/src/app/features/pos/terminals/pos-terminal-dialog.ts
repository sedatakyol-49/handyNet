import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { UI } from '../../../shared/ui';
import { TerminalService } from './terminal.service';
import { SimulatedChoice } from './terminal-transports';

/**
 * Shown by the POS shell while a card terminal run is in progress (TerminalService.active): for the simulated terminal
 * the cashier picks the outcome (clearly marked TEST); for a real terminal it shows what the terminal is doing and
 * offers to request an abort. It cannot be dismissed — the run always ends with a defined result.
 */
@Component({
  selector: 'hn-pos-terminal-dialog',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [title]="title() | t" [open]="!!run()" [guardClose]="true" testId="pos-terminal-dialog">
      @if (run(); as r) {
        <p class="text-center text-sm text-pos-muted">{{ r.profile.name }}</p>
        <p class="text-center text-4xl font-bold tracking-tight tabular-nums" data-testid="pos-terminal-amount">{{ r.amountMinor | money }}</p>

        @if (r.phase === 'choose') {
          <p class="mt-3 rounded-[10px] bg-pos-warn-soft px-3 py-2 text-sm font-semibold text-pos-ink" role="note" data-testid="pos-terminal-test-note">
            {{ 'pos.terminals.sim.note' | t }}
          </p>
          <div class="mt-3 grid grid-cols-2 gap-2" role="group" [attr.aria-label]="'pos.terminals.sim.choose' | t">
            @for (c of choices(); track c) {
              <button type="button" class="pos-soft min-h-14 text-base" [class.pos-soft-accent]="c === 'approve'" (click)="terminals.chooseSimulated(c)" [attr.data-testid]="'pos-terminal-sim-' + c">
                {{ 'pos.terminals.sim.' + c | t }}
              </button>
            }
          </div>
        } @else if (r.phase === 'waiting') {
          <div class="mt-4 flex flex-col items-center gap-3 text-center" role="status" aria-live="polite">
            <span class="size-10 animate-spin rounded-full border-4 border-pos-accent border-r-transparent" aria-hidden="true"></span>
            <p class="text-base font-semibold" data-testid="pos-terminal-progress">{{ r.progress ?? ('pos.terminals.run.waiting' | t) }}</p>
            <p class="text-sm text-pos-muted">{{ 'pos.terminals.run.hint' | t }}</p>
          </div>
          <div class="mt-4 flex justify-center">
            <button type="button" class="pos-soft min-h-12" [disabled]="r.abortRequested" (click)="terminals.requestAbort()" data-testid="pos-terminal-abort">
              {{ (r.abortRequested ? 'pos.terminals.run.abortRequested' : 'pos.terminals.run.abort') | t }}
            </button>
          </div>
        } @else {
          <div class="mt-4 flex flex-col items-center gap-3 text-center" role="status" aria-live="polite">
            <span class="size-10 animate-spin rounded-full border-4 border-pos-accent border-r-transparent" aria-hidden="true"></span>
            <p class="text-base font-semibold">{{ 'pos.terminals.run.saving' | t }}</p>
          </div>
        }
      }
    </hn-dialog>
  `,
})
export class PosTerminalDialogComponent {
  protected readonly terminals = inject(TerminalService);
  protected readonly run = this.terminals.active;
  protected readonly title = computed(() => {
    const r = this.run();
    return r ? `pos.terminals.run.title.${r.operation}` : 'pos.terminals.run.title.payment';
  });
  /** A reversal cannot be "aborted by the customer". */
  protected readonly choices = computed<SimulatedChoice[]>(() => (this.run()?.operation === 'reversal' ? ['approve', 'decline', 'timeout'] : ['approve', 'decline', 'abort', 'timeout']));
}
