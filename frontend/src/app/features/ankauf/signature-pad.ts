import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, input, output, signal, viewChild } from '@angular/core';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

/** Minimum drawn extent (CSS px) and points so that a tap or a dot is not taken as a signature. */
export const MIN_SIGNATURE_POINTS = 8;
export const MIN_SIGNATURE_EXTENT = 40;

export interface StrokeStats {
  points: number;
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

/** Whether the strokes look like a signature (enough points and a minimal width or height). */
export function isSignatureLike(s: StrokeStats): boolean {
  return s.points >= MIN_SIGNATURE_POINTS && Math.max(s.maxX - s.minX, s.maxY - s.minY) >= MIN_SIGNATURE_EXTENT;
}

/**
 * Signature on the screen: touch, pen or mouse (pointer events, pressure-aware line width), exported as PNG with a
 * transparent background. Emits the PNG data URL after each stroke, or null when cleared / not yet signature-like.
 */
@Component({
  selector: 'hn-signature-pad',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="relative rounded-xl border-2 border-dashed bg-white transition-colors"
      [class]="signed() ? 'border-emerald-500' : 'border-slate-300 hover:border-accent-600 focus-within:border-accent-600'">
      <canvas #canvas class="block h-44 w-full touch-none select-none rounded-xl sm:h-52" role="img" tabindex="0"
        [attr.aria-label]="label() || ('ankauf.signature.padLabel' | t)" [attr.aria-describedby]="hintId()"
        (pointerdown)="down($event)" (pointermove)="move($event)" (pointerup)="up($event)" (pointercancel)="up($event)" (pointerleave)="up($event)"
        [attr.data-testid]="testId()"></canvas>
      <div class="pointer-events-none absolute inset-x-6 bottom-9 border-b border-slate-300" aria-hidden="true"></div>
      <span class="pointer-events-none absolute start-6 bottom-10 text-lg text-slate-400" aria-hidden="true">✕</span>
      @if (!drawn()) {
        <p class="pointer-events-none absolute inset-0 grid place-items-center text-sm text-slate-400" aria-hidden="true">{{ 'ankauf.signature.placeholder' | t }}</p>
      }
    </div>
    <div class="mt-2 flex flex-wrap items-center justify-between gap-2 text-xs">
      <p [id]="hintId()" class="text-slate-600">{{ 'ankauf.signature.hint' | t }}</p>
      <button type="button" class="hn-link min-h-11 px-2 font-medium" (click)="clear()" [disabled]="!drawn()" [attr.data-testid]="testId() + '-clear'">
        {{ 'ankauf.signature.clear' | t }}
      </button>
    </div>
  `,
})
export class SignaturePadComponent {
  readonly label = input<string | null>(null);
  readonly testId = input('signature-pad');
  readonly changed = output<string | null>();

  private static counter = 0;
  protected readonly hintId = signal(`sig-hint-${++SignaturePadComponent.counter}`);
  protected readonly drawn = signal(false);
  protected readonly signed = signal(false);
  private readonly canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private ctx: CanvasRenderingContext2D | null = null;
  private drawing = false;
  private last: { x: number; y: number } | null = null;
  private stats: StrokeStats = { points: 0, minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity };

  constructor() {
    afterNextRender(() => this.setup());
  }

  private setup(): void {
    const canvas = this.canvas().nativeElement;
    const rect = canvas.getBoundingClientRect();
    // At most 2× pixel density: the API accepts up to 2400 × 1200 px.
    const ratio = Math.min(2, Math.max(1, globalThis.devicePixelRatio || 1));
    canvas.width = Math.max(100, Math.round(rect.width * ratio));
    canvas.height = Math.max(60, Math.round(rect.height * ratio));
    this.ctx = canvas.getContext('2d');
    if (!this.ctx) return;
    this.ctx.scale(ratio, ratio);
    this.ctx.lineCap = 'round';
    this.ctx.lineJoin = 'round';
    this.ctx.strokeStyle = '#0f172a';
  }

  private point(e: PointerEvent): { x: number; y: number } {
    const rect = this.canvas().nativeElement.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  }

  protected down(e: PointerEvent): void {
    if (!this.ctx) this.setup();
    if (!this.ctx) return;
    e.preventDefault();
    this.canvas().nativeElement.setPointerCapture?.(e.pointerId);
    this.drawing = true;
    this.last = this.point(e);
    this.track(this.last);
    this.ctx.beginPath();
    this.ctx.arc(this.last.x, this.last.y, 1.2, 0, Math.PI * 2);
    this.ctx.fillStyle = '#0f172a';
    this.ctx.fill();
    this.drawn.set(true);
  }

  protected move(e: PointerEvent): void {
    if (!this.drawing || !this.ctx || !this.last) return;
    e.preventDefault();
    const p = this.point(e);
    const pressure = e.pointerType === 'pen' && e.pressure > 0 ? e.pressure : 0.5;
    this.ctx.lineWidth = 1.2 + pressure * 2.4;
    this.ctx.beginPath();
    this.ctx.moveTo(this.last.x, this.last.y);
    this.ctx.lineTo(p.x, p.y);
    this.ctx.stroke();
    this.last = p;
    this.track(p);
  }

  protected up(e: PointerEvent): void {
    if (!this.drawing) return;
    this.drawing = false;
    this.last = null;
    this.canvas().nativeElement.releasePointerCapture?.(e.pointerId);
    const ok = isSignatureLike(this.stats);
    this.signed.set(ok);
    this.changed.emit(ok ? this.canvas().nativeElement.toDataURL('image/png') : null);
  }

  /** Clears the pad (e.g. after the data changed and a new signature is needed). */
  clear(): void {
    const canvas = this.canvas().nativeElement;
    this.ctx?.clearRect(0, 0, canvas.width, canvas.height);
    this.stats = { points: 0, minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity };
    this.drawn.set(false);
    this.signed.set(false);
    this.changed.emit(null);
  }

  private track(p: { x: number; y: number }): void {
    const s = this.stats;
    this.stats = { points: s.points + 1, minX: Math.min(s.minX, p.x), minY: Math.min(s.minY, p.y), maxX: Math.max(s.maxX, p.x), maxY: Math.max(s.maxY, p.y) };
  }
}
