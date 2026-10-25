import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Order } from '../../core/api/models';
import { UI } from '../../shared/ui';

export type TimelineState = 'done' | 'current' | 'todo' | 'stopped';

export interface TimelineStep {
  key: 'placed' | 'confirmed' | 'shipped' | 'delivered' | 'completed';
  at: string | null;
  state: TimelineState;
}

/**
 * The B2B order lifecycle as steps: placed → confirmed → shipped → delivered → completed. Cancelled/rejected orders stop
 * at the step they reached; disputed/refunded orders stop after delivery.
 */
export function orderTimeline(
  o: Pick<Order, 'status' | 'placedAt' | 'confirmedAt' | 'shippedAt' | 'deliveredAt' | 'completedAt'>,
): TimelineStep[] {
  const steps: Omit<TimelineStep, 'state'>[] = [
    { key: 'placed', at: o.placedAt },
    { key: 'confirmed', at: o.confirmedAt },
    { key: 'shipped', at: o.shippedAt },
    { key: 'delivered', at: o.deliveredAt ?? null },
    { key: 'completed', at: o.completedAt ?? null },
  ];
  const reached = steps.reduce((last, s, i) => (s.at ? i : last), 0);
  const stopped = o.status === 'Cancelled' || o.status === 'Rejected' || o.status === 'Disputed' || o.status === 'Refunded';
  return steps.map((s, i) => ({
    ...s,
    state: i <= reached && (s.at || i === 0) ? 'done' : stopped ? (i === reached + 1 ? 'stopped' : 'todo') : i === reached + 1 ? 'current' : 'todo',
  }));
}

@Component({
  selector: 'hn-order-timeline',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <ol class="grid grid-cols-5 gap-1" [attr.aria-label]="'orders.timeline.label' | t" data-testid="order-timeline">
      @for (s of steps(); track s.key; let last = $last) {
        <li class="relative flex flex-col items-center text-center" [attr.data-step]="s.key" [attr.data-state]="s.state"
          [attr.aria-current]="s.state === 'current' ? 'step' : null">
          @if (!last) {
            <span class="absolute start-1/2 top-4 h-0.5 w-full" [class]="s.state === 'done' ? 'bg-emerald-500' : 'bg-slate-200'" aria-hidden="true"></span>
          }
          <span class="relative z-10 grid size-8 place-items-center rounded-full text-sm font-bold ring-4 ring-white"
            [class]="s.state === 'done' ? 'bg-emerald-600 text-white' : s.state === 'current' ? 'bg-accent-600 text-white' : s.state === 'stopped' ? 'bg-red-100 text-red-700' : 'bg-slate-100 text-slate-400'"
            aria-hidden="true">{{ s.state === 'done' ? '✓' : s.state === 'stopped' ? '✕' : $index + 1 }}</span>
          <span class="mt-1 text-xs font-medium" [class.text-slate-400]="s.state === 'todo'">{{ 'orders.timeline.' + s.key | t }}</span>
          <span class="text-[11px] text-slate-500">{{ s.at ? (s.at | hnDate: 'date') : '' }}</span>
        </li>
      }
    </ol>
    @if (closed()) {
      <p class="mt-2 text-center text-xs font-medium text-red-700">{{ status() | tEnum: 'orderStatus' }}</p>
    }
  `,
})
export class OrderTimelineComponent {
  readonly order = input.required<Order>();
  protected readonly steps = computed(() => orderTimeline(this.order()));
  protected readonly status = computed(() => this.order().status);
  protected readonly closed = computed(() => ['Cancelled', 'Rejected', 'Disputed', 'Refunded'].includes(this.order().status));
}
