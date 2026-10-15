import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DisputeMessage, DisputeParty } from '../../../core/api/models';
import { UI } from '../../../shared/ui';
import { chronological } from './dispute.logic';

/**
 * Read-only message thread of a dispute (chronological). Messages of `own` are aligned to the end and tinted;
 * the sender label (Käufer / Verkäufer / Plattform) is always shown as text, never only by color.
 */
@Component({
  selector: 'hn-dispute-thread',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="space-y-2" [attr.aria-label]="'disputes.thread' | t" data-testid="dispute-thread">
      @for (m of sorted(); track $index) {
        <li class="flex" [class.justify-end]="m.from === own()" data-testid="dispute-message" [attr.data-from]="m.from" [attr.data-own]="m.from === own()">
          <div
            class="max-w-[42rem] rounded-md border px-3 py-2 text-sm"
            [class]="
              m.from === own()
                ? 'border-blue-200 bg-blue-50'
                : m.from === 'Platform'
                  ? 'border-amber-200 bg-amber-50'
                  : 'border-slate-200 bg-white'
            "
          >
            <p class="mb-0.5 flex flex-wrap items-baseline gap-x-2 text-xs text-slate-600">
              <span class="font-semibold text-slate-800">{{ m.from | tEnum: 'disputeParty' }}</span>
              @if (m.from === own()) {
                <span>({{ 'disputes.you' | t }})</span>
              }
              <time class="tabular-nums" [attr.datetime]="m.sentAt">{{ m.sentAt | hnDate }}</time>
            </p>
            <p class="break-words whitespace-pre-line">{{ m.text }}</p>
          </div>
        </li>
      } @empty {
        <li class="hn-muted text-sm">{{ 'disputes.noMessages' | t }}</li>
      }
    </ol>
  `,
})
export class DisputeThreadComponent {
  readonly messages = input.required<DisputeMessage[]>();
  /** Party of the viewer (Buyer / Seller / Platform). */
  readonly own = input<DisputeParty | null>(null);
  protected readonly sorted = computed(() => chronological(this.messages()));
}
