import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { CatalogApi } from '../../core/api/handynet-api';
import { ProductImage } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

/**
 * Read-only gallery: large view + thumbnail strip. Keyboard: arrow keys on the large view or a thumbnail switch images
 * (mirrored for RTL), Home/End jump to the first/last one. Placeholder when the product has no images.
 */
@Component({
  selector: 'hn-image-gallery',
  imports: [TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @if (sorted().length === 0) {
      <div
        class="flex aspect-[4/3] w-full flex-col items-center justify-center rounded-md border border-dashed border-slate-300 bg-slate-50 text-sm text-slate-500"
        data-testid="gallery-placeholder"
      >
        <svg viewBox="0 0 24 24" class="mb-2 size-10 text-slate-300" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true">
          <rect x="3" y="4" width="18" height="16" rx="2" />
          <circle cx="9" cy="10" r="2" />
          <path d="m21 16-5-5-8 8" />
        </svg>
        {{ 'images.noImage' | t }}
      </div>
    } @else {
      <div
        class="relative flex aspect-[4/3] w-full items-center justify-center rounded-md border border-slate-200 bg-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent-600"
        tabindex="0"
        role="group"
        [attr.aria-roledescription]="'images.gallery' | t"
        [attr.aria-label]="'images.position' | t: { n: index() + 1, total: sorted().length }"
        (keydown)="onKey($event)"
        data-testid="gallery-main"
      >
        <img [src]="api.imageUrl(current()?.fullUrl)" [alt]="alt(index())" class="size-full object-contain" />
        @if (sorted().length > 1) {
          <button type="button" class="absolute start-1 top-1/2 -translate-y-1/2 rounded bg-white/90 px-2 py-1 text-slate-700 shadow-sm hover:bg-white hover:text-slate-950 active:bg-slate-100"
            (click)="go(index() - 1)" [attr.aria-label]="'images.previous' | t" [title]="'images.previous' | t">
            <span aria-hidden="true" class="inline-block rtl:rotate-180">◀</span>
          </button>
          <button type="button" class="absolute end-1 top-1/2 -translate-y-1/2 rounded bg-white/90 px-2 py-1 text-slate-700 shadow-sm hover:bg-white hover:text-slate-950 active:bg-slate-100"
            (click)="go(index() + 1)" [attr.aria-label]="'images.next' | t" [title]="'images.next' | t">
            <span aria-hidden="true" class="inline-block rtl:rotate-180">▶</span>
          </button>
          <span class="absolute bottom-1 end-1 rounded bg-slate-900/70 px-1.5 text-[11px] text-white" dir="ltr" aria-hidden="true">
            {{ index() + 1 }} / {{ sorted().length }}
          </span>
        }
      </div>
      @if (sorted().length > 1) {
        <ul class="mt-2 flex flex-wrap gap-1.5" [attr.aria-label]="'images.thumbnails' | t">
          @for (img of sorted(); track img.id; let i = $index) {
            <li>
              <button
                type="button"
                class="block size-14 overflow-hidden rounded border bg-white focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-accent-600"
                [class.border-accent-600]="i === index()"
                [class.ring-1]="i === index()"
                [class.border-slate-200]="i !== index()"
                [attr.aria-current]="i === index() ? 'true' : null"
                [attr.aria-label]="alt(i)"
                (click)="go(i)"
                (keydown)="onKey($event)"
                data-testid="gallery-thumb"
              >
                <img [src]="api.imageUrl(img.thumbnailUrl)" alt="" class="size-full object-contain" loading="lazy" />
              </button>
            </li>
          }
        </ul>
      }
    }
  `,
})
export class ImageGalleryComponent {
  protected readonly api = inject(CatalogApi);
  private readonly i18n = inject(I18nService);

  readonly images = input<ProductImage[]>([]);
  readonly productName = input('');

  protected readonly index = signal(0);
  protected readonly sorted = computed(() => [...this.images()].sort((a, b) => a.position - b.position));
  protected readonly current = computed(() => this.sorted()[this.index()] ?? null);

  constructor() {
    // A different product (other images) starts at its primary image.
    effect(() => {
      this.images();
      this.index.set(0);
    });
  }

  protected alt(i: number): string {
    return this.i18n.t('images.altOf', { name: this.productName(), n: i + 1, total: this.sorted().length });
  }

  protected go(i: number): void {
    const n = this.sorted().length;
    if (n === 0) return;
    this.index.set(((i % n) + n) % n);
  }

  protected onKey(event: KeyboardEvent): void {
    const rtl = this.i18n.dir() === 'rtl';
    const next = rtl ? 'ArrowLeft' : 'ArrowRight';
    const prev = rtl ? 'ArrowRight' : 'ArrowLeft';
    if (event.key === next) this.go(this.index() + 1);
    else if (event.key === prev) this.go(this.index() - 1);
    else if (event.key === 'Home') this.go(0);
    else if (event.key === 'End') this.go(this.sorted().length - 1);
    else return;
    event.preventDefault();
  }
}
