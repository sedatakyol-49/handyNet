import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, output, signal, viewChild } from '@angular/core';
import { I18nService } from '../../core/i18n/i18n.service';
import { UI } from '../../shared/ui';
import { MrzData, mrzNameMayBeTransliterated, parseMrz } from './mrz';

export interface MrzCapture {
  data: MrzData;
  /** Normalized MRZ lines, sent to the API which verifies them again. */
  raw: string;
}

/**
 * MRZ field for keyboard-wedge document scanners and manual paste: focus the field, scan (or paste) the 2–3 lines of the
 * machine-readable zone; check digits are verified live and the form is filled as soon as the MRZ is complete and valid.
 */
@Component({
  selector: 'hn-mrz-input',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="space-y-3">
      <ol class="grid gap-2 text-xs text-slate-600 sm:grid-cols-3" aria-hidden="true">
        <li class="flex items-start gap-2"><span class="grid size-5 shrink-0 place-items-center rounded-full bg-accent-50 font-semibold text-accent-800">1</span>{{ 'ankauf.mrz.step1' | t }}</li>
        <li class="flex items-start gap-2"><span class="grid size-5 shrink-0 place-items-center rounded-full bg-accent-50 font-semibold text-accent-800">2</span>{{ 'ankauf.mrz.step2' | t }}</li>
        <li class="flex items-start gap-2"><span class="grid size-5 shrink-0 place-items-center rounded-full bg-accent-50 font-semibold text-accent-800">3</span>{{ 'ankauf.mrz.step3' | t }}</li>
      </ol>
      <hn-field [label]="'ankauf.mrz.label' | t" for="in-mrz" [hint]="'ankauf.mrz.hint' | t">
        <textarea #area hnInput id="in-mrz" rows="3" spellcheck="false" autocomplete="off" autocapitalize="characters" dir="ltr"
          class="hn-mono resize-none text-[13px] leading-5 tracking-wider uppercase" [placeholder]="placeholder"
          [value]="text()" (input)="onInput($any($event.target).value)" (keydown.enter)="onEnter($event)"
          [attr.aria-invalid]="state() === 'error'" aria-describedby="in-mrz-status" data-testid="in-mrz"></textarea>
      </hn-field>
      <div id="in-mrz-status" role="status" aria-live="polite" class="flex min-h-6 flex-wrap items-center gap-2 text-sm" data-testid="in-mrz-status"
        [attr.data-state]="state()">
        @switch (state()) {
          @case ('idle') {
            <span class="text-slate-500">{{ 'ankauf.mrz.waiting' | t }}</span>
          }
          @case ('typing') {
            <span class="inline-block size-2 animate-pulse rounded-full bg-accent-600" aria-hidden="true"></span>
            <span class="text-slate-600">{{ 'ankauf.mrz.reading' | t }}</span>
          }
          @case ('ok') {
            <span class="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-2.5 py-0.5 font-medium text-emerald-800">✓ {{ 'ankauf.mrz.valid' | t }}</span>
            <span class="text-slate-600">{{ 'ankauf.mrz.applied' | t }}</span>
            @if (transliterated()) {
              <span class="w-full text-xs text-amber-800">{{ 'ankauf.mrz.transliterated' | t }}</span>
            }
          }
          @case ('error') {
            <span class="inline-flex items-center gap-1 rounded-full bg-red-50 px-2.5 py-0.5 font-medium text-red-800">{{ errorText() }}</span>
          }
        }
      </div>
      <div class="flex flex-wrap gap-2">
        <button hnButton size="sm" type="button" (click)="focus()" data-testid="in-mrz-focus">{{ 'ankauf.mrz.ready' | t }}</button>
        @if (text()) {
          <button hnButton size="sm" variant="ghost" type="button" (click)="reset()">{{ 'common.reset' | t }}</button>
        }
      </div>
    </div>
  `,
})
export class MrzInputComponent {
  private readonly i18n = inject(I18nService);
  readonly captured = output<MrzCapture>();
  private readonly area = viewChild<ElementRef<HTMLTextAreaElement>>('area');

  protected readonly placeholder = 'IDD<<T220001293<<<<<<<<<<<<<<<\n6408125<2010315D<<<<<<<<<<<<<4\nMUSTERMANN<<ERIKA<<<<<<<<<<<<<';
  protected readonly text = signal('');
  private readonly result = computed(() => parseMrz(this.text()));
  protected readonly state = computed<'idle' | 'typing' | 'ok' | 'error'>(() => {
    const r = this.result();
    if (r.ok) return 'ok';
    if (r.error === 'empty') return 'idle';
    if (r.error === 'incomplete') return 'typing';
    return 'error';
  });
  protected readonly transliterated = computed(() => {
    const r = this.result();
    return r.ok && (mrzNameMayBeTransliterated(r.data.surname) || mrzNameMayBeTransliterated(r.data.givenNames));
  });
  protected readonly errorText = computed(() => {
    const r = this.result();
    if (r.ok) return '';
    return r.error === 'checkDigit'
      ? this.i18n.t('ankauf.mrz.checkDigit', { field: this.i18n.t('ankauf.mrz.fields.' + (r.field ?? 'composite')) })
      : this.i18n.t('ankauf.mrz.' + (r.error === 'date' ? 'date' : 'format'));
  });
  private lastEmitted = '';

  protected onInput(value: string): void {
    this.text.set(value);
    const r = this.result();
    if (r.ok) {
      const raw = r.lines.join('\n');
      if (raw !== this.lastEmitted) {
        this.lastEmitted = raw;
        this.captured.emit({ data: r.data, raw });
      }
    }
  }

  /** Scanners end each line with Enter: keep it as a line break, but never submit the surrounding form. */
  protected onEnter(e: Event): void {
    e.stopPropagation();
  }

  focus(): void {
    this.area()?.nativeElement.focus();
  }

  protected reset(): void {
    this.text.set('');
    this.lastEmitted = '';
    this.focus();
  }
}
