import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, computed, effect, input, output, signal, viewChild } from '@angular/core';
import { UI } from '../../shared/ui';
import { CropAspect, cropRegion, fitWithin } from './company.logic';

/** Longest side of the cropped upload (the API scales again: 480 px screen PNG, 1200 px print JPEG). */
const OUTPUT_MAX_SIDE = 1600;

/**
 * Crop and preview a logo before it is uploaded: aspect (as is / square / wide 3:1), zoom and position, with a live preview
 * on a light and a dark background. Emits the cropped image as PNG (transparency kept), or the original file unchanged.
 */
@Component({
  selector: 'hn-logo-crop-dialog',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hn-dialog [open]="!!file()" [title]="'company.logo.cropTitle' | t" [wide]="true" (closed)="cancelled.emit()">
      <div class="grid gap-4 md:grid-cols-[1fr_16rem]" data-testid="logo-crop">
        <div class="space-y-3">
          <div class="grid place-items-center rounded-md border border-slate-200 bg-[conic-gradient(#f1f5f9_25%,#fff_0_50%,#f1f5f9_0_75%,#fff_0)] bg-[length:16px_16px] p-3">
            <canvas #preview class="max-h-56 max-w-full" [attr.aria-label]="'company.logo.previewLabel' | t" role="img" data-testid="logo-crop-preview"></canvas>
          </div>
          <div class="flex flex-wrap items-center gap-3 text-xs text-slate-600">
            <span class="flex items-center gap-2 rounded bg-white px-2 py-1 ring-1 ring-slate-200">
              <canvas #light class="h-6 w-auto"></canvas>{{ 'company.logo.onLight' | t }}
            </span>
            <span class="flex items-center gap-2 rounded bg-slate-900 px-2 py-1 text-white">
              <canvas #dark class="h-6 w-auto"></canvas>{{ 'company.logo.onDark' | t }}
            </span>
            @if (size(); as s) {
              <span data-testid="logo-crop-size">{{ s.width }} × {{ s.height }} px</span>
            }
          </div>
        </div>

        <div class="space-y-4">
          <fieldset>
            <legend class="mb-1 text-xs font-medium text-slate-700">{{ 'company.logo.aspect' | t }}</legend>
            <div class="flex flex-wrap gap-1" role="radiogroup">
              @for (a of aspects; track a) {
                <label>
                  <input type="radio" name="logo-aspect" class="sr-only peer" [value]="a" [checked]="aspect() === a" (change)="aspect.set(a)"
                    [attr.data-testid]="'logo-aspect-' + a" />
                  <span class="block rounded-md border border-slate-300 px-3 py-2 text-sm peer-checked:border-slate-900 peer-checked:bg-slate-900 peer-checked:text-white peer-focus-visible:ring-2 peer-focus-visible:ring-accent-600 hover:bg-slate-100 peer-checked:hover:bg-slate-800">
                    {{ 'company.logo.aspects.' + a | t }}
                  </span>
                </label>
              }
            </div>
          </fieldset>
          <label class="block text-xs font-medium text-slate-700" for="logo-zoom">{{ 'company.logo.zoom' | t }}
            <input id="logo-zoom" type="range" min="1" max="4" step="0.05" class="mt-1 block w-full accent-slate-900" [value]="zoom()"
              (input)="zoom.set(+$any($event.target).value)" data-testid="logo-zoom" />
          </label>
          <label class="block text-xs font-medium text-slate-700" for="logo-pan-x">{{ 'company.logo.horizontal' | t }}
            <input id="logo-pan-x" type="range" min="-1" max="1" step="0.02" class="mt-1 block w-full accent-slate-900" [value]="panX()"
              [disabled]="zoom() === 1 && aspect() === 'free'" (input)="panX.set(+$any($event.target).value)" />
          </label>
          <label class="block text-xs font-medium text-slate-700" for="logo-pan-y">{{ 'company.logo.vertical' | t }}
            <input id="logo-pan-y" type="range" min="-1" max="1" step="0.02" class="mt-1 block w-full accent-slate-900" [value]="panY()"
              [disabled]="zoom() === 1 && aspect() === 'free'" (input)="panY.set(+$any($event.target).value)" />
          </label>
          <button hnButton variant="ghost" size="sm" type="button" (click)="reset()">{{ 'company.logo.resetCrop' | t }}</button>
        </div>
      </div>
      @if (loadError()) {
        <hn-banner class="mt-3" [text]="'company.logo.errors.read' | t" />
      }
      <div class="mt-4 flex flex-wrap justify-end gap-2">
        <button hnButton type="button" (click)="cancelled.emit()">{{ 'common.cancel' | t }}</button>
        <button hnButton type="button" [disabled]="busy()" (click)="useOriginal()" data-testid="logo-use-original">{{ 'company.logo.useOriginal' | t }}</button>
        <button hnButton variant="primary" type="button" [loading]="busy()" [disabled]="busy() || !image()" (click)="confirm()" data-testid="logo-crop-confirm">
          {{ 'company.logo.uploadCropped' | t }}
        </button>
      </div>
    </hn-dialog>
  `,
})
export class LogoCropDialogComponent implements OnDestroy {
  readonly file = input<File | null>(null);
  readonly busy = input(false);
  readonly cropped = output<{ blob: Blob; fileName: string }>();
  readonly cancelled = output<void>();

  protected readonly aspects: CropAspect[] = ['free', 'square', 'wide'];
  protected readonly aspect = signal<CropAspect>('free');
  protected readonly zoom = signal(1);
  protected readonly panX = signal(0);
  protected readonly panY = signal(0);
  protected readonly image = signal<HTMLImageElement | null>(null);
  protected readonly loadError = signal(false);

  private readonly preview = viewChild<ElementRef<HTMLCanvasElement>>('preview');
  private readonly light = viewChild<ElementRef<HTMLCanvasElement>>('light');
  private readonly dark = viewChild<ElementRef<HTMLCanvasElement>>('dark');
  private objectUrl: string | null = null;

  protected readonly region = computed(() => {
    const img = this.image();
    return img ? cropRegion(img.naturalWidth, img.naturalHeight, this.aspect(), this.zoom(), this.panX(), this.panY()) : null;
  });

  protected readonly size = computed(() => {
    const r = this.region();
    return r ? fitWithin(r.width, r.height, OUTPUT_MAX_SIDE) : null;
  });

  constructor() {
    effect(() => {
      const file = this.file();
      this.release();
      this.image.set(null);
      this.loadError.set(false);
      this.reset();
      if (!file || typeof URL === 'undefined' || typeof Image === 'undefined') return;
      const url = URL.createObjectURL(file);
      this.objectUrl = url;
      const img = new Image();
      img.onload = () => this.image.set(img);
      img.onerror = () => this.loadError.set(true);
      img.src = url;
    });
    effect(() => {
      const img = this.image();
      const r = this.region();
      if (!img || !r) return;
      const preview = this.preview()?.nativeElement;
      const size = fitWithin(r.width, r.height, 480);
      for (const canvas of [preview, this.light()?.nativeElement, this.dark()?.nativeElement]) {
        if (!canvas) continue;
        const s = canvas === preview ? size : fitWithin(r.width, r.height, 96);
        draw(canvas, img, r, s.width, s.height);
      }
    });
  }

  ngOnDestroy(): void {
    this.release();
  }

  protected reset(): void {
    this.aspect.set('free');
    this.zoom.set(1);
    this.panX.set(0);
    this.panY.set(0);
  }

  protected useOriginal(): void {
    const file = this.file();
    if (file) this.cropped.emit({ blob: file, fileName: file.name });
  }

  protected confirm(): void {
    const img = this.image();
    const r = this.region();
    const s = this.size();
    if (!img || !r || !s) return;
    const canvas = document.createElement('canvas');
    draw(canvas, img, r, s.width, s.height);
    canvas.toBlob((blob) => {
      if (blob) this.cropped.emit({ blob, fileName: 'logo.png' });
      else this.loadError.set(true);
    }, 'image/png');
  }

  private release(): void {
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
  }
}

function draw(canvas: HTMLCanvasElement, img: HTMLImageElement, r: { x: number; y: number; width: number; height: number }, w: number, h: number): void {
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return;
  ctx.clearRect(0, 0, w, h);
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(img, r.x, r.y, r.width, r.height, 0, 0, w, h);
}
