import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, inject, input, output, signal, viewChild } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { toApiProblem } from '../../core/api/api-problem';
import { CatalogApi } from '../../core/api/handynet-api';
import { ProductImage } from '../../core/api/models';
import { I18nService } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { createQuery } from '../data/query';
import { UI } from '../ui';
import { ACCEPTED_IMAGE_TYPES, MAX_IMAGES_PER_PRODUCT, checkImageFile, moveItem, sameOrder } from './image-rules';

interface PendingUpload {
  id: number;
  name: string;
  previewUrl: string | null;
  error: string | null;
}

/**
 * Images of one catalog product: list with primary badge; when `canManage` (the organization created the product):
 * upload with preview (button or drop files), reorder by drag & drop or with the "earlier/later" buttons (keyboard),
 * "Als Hauptbild", remove with inline confirmation. Every change goes to the API and the list is re-read from it.
 */
@Component({
  selector: 'hn-product-image-manager',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    <div class="space-y-3" data-testid="image-manager">
      <hn-banner [text]="error()" [dismissible]="true" (dismissed)="error.set(null)" />
      <p class="sr-only" aria-live="polite">{{ announcement() }}</p>

      <ol class="grid grid-cols-[repeat(auto-fill,minmax(8.5rem,1fr))] gap-2" [attr.aria-label]="'images.listLabel' | t">
        @for (img of ordered(); track img.id; let i = $index; let first = $first; let last = $last) {
          <li
            class="relative rounded-md border bg-white p-1"
            [class.border-slate-200]="dragOver() !== i"
            [class.border-blue-500]="dragOver() === i"
            [attr.draggable]="canManage() && !busy()"
            (dragstart)="onDragStart($event, i)"
            (dragover)="onDragOver($event, i)"
            (dragleave)="dragOver.set(null)"
            (drop)="onDrop($event, i)"
            (dragend)="dragFrom = null; dragOver.set(null)"
            data-testid="image-item"
          >
            <img
              [src]="api.imageUrl(img.thumbnailUrl)"
              [alt]="'images.imageAlt' | t: { n: i + 1 }"
              class="aspect-square w-full rounded object-contain bg-slate-50"
              loading="lazy"
              width="160"
              height="160"
            />
            @if (first) {
              <span class="absolute start-2 top-2 rounded bg-slate-900 px-1.5 py-0.5 text-[10px] font-semibold text-white" data-testid="primary-badge">
                {{ 'images.primary' | t }}
              </span>
            }
            @if (canManage()) {
              <div class="mt-1 flex flex-wrap items-center gap-1">
                <button hnButton size="sm" variant="ghost" type="button" [disabled]="first || busy()" (click)="move(i, i - 1)"
                  [attr.aria-label]="'images.moveEarlier' | t: { n: i + 1 }">
                  <span aria-hidden="true" class="rtl:rotate-180 inline-block">◀</span>
                </button>
                <button hnButton size="sm" variant="ghost" type="button" [disabled]="last || busy()" (click)="move(i, i + 1)"
                  [attr.aria-label]="'images.moveLater' | t: { n: i + 1 }">
                  <span aria-hidden="true" class="rtl:rotate-180 inline-block">▶</span>
                </button>
                @if (!first) {
                  <button hnButton size="sm" variant="ghost" type="button" [disabled]="busy()" (click)="move(i, 0)">{{ 'images.makePrimary' | t }}</button>
                }
                @if (confirmDelete() === img.id) {
                  <button hnButton size="sm" variant="danger" type="button" [disabled]="busy()" (click)="remove(img)" data-testid="image-delete-confirm">
                    {{ 'images.confirmDelete' | t }}
                  </button>
                  <button hnButton size="sm" variant="ghost" type="button" (click)="confirmDelete.set(null)">{{ 'common.cancel' | t }}</button>
                } @else {
                  <button hnButton size="sm" variant="ghost" type="button" [disabled]="busy()" (click)="confirmDelete.set(img.id)"
                    [attr.aria-label]="'images.delete' | t: { n: i + 1 }" data-testid="image-delete">
                    {{ 'common.remove' | t }}
                  </button>
                }
              </div>
            }
          </li>
        }
        @for (p of pending(); track p.id) {
          <li class="relative rounded-md border border-dashed border-slate-300 p-1" data-testid="image-pending">
            @if (p.previewUrl) {
              <img [src]="p.previewUrl" alt="" class="aspect-square w-full rounded object-contain opacity-60" />
            }
            <p class="mt-1 truncate text-xs text-slate-600" [title]="p.name">{{ p.error ?? ('images.uploading' | t) }}</p>
          </li>
        }
      </ol>

      @if (!images.loading() && ordered().length === 0 && pending().length === 0) {
        <p class="text-sm text-slate-500">{{ 'images.none' | t }}</p>
      }

      @if (canManage()) {
        <div
          class="rounded-md border border-dashed px-3 py-3 text-sm"
          [class.border-slate-300]="!fileDragOver()"
          [class.border-blue-500]="fileDragOver()"
          [class.bg-blue-50]="fileDragOver()"
          (dragover)="onFileDragOver($event)"
          (dragleave)="fileDragOver.set(false)"
          (drop)="onFileDrop($event)"
        >
          <div class="flex flex-wrap items-center gap-3">
            <input
              #fileInput
              type="file"
              class="peer sr-only"
              [id]="inputId"
              [disabled]="full() || busy()"
              [accept]="accept"
              multiple
              (change)="onFiles($any($event.target).files); $any($event.target).value = ''"
              data-testid="image-file"
            />
            <label
              [for]="inputId"
              class="inline-flex cursor-pointer items-center rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-800 shadow-xs hover:bg-slate-50 peer-focus-visible:outline-2 peer-focus-visible:outline-offset-2 peer-focus-visible:outline-accent-600 peer-disabled:cursor-not-allowed peer-disabled:opacity-50"
            >
              {{ 'images.upload' | t }}
            </label>
            <span class="text-xs text-slate-500">{{ 'images.uploadHint' | t: { max: maxImages, count: ordered().length } }}</span>
          </div>
        </div>
      } @else if (ordered().length > 0 || readOnlyNote()) {
        <p class="text-xs text-slate-500">{{ 'images.readOnly' | t }}</p>
      }
    </div>
  `,
})
export class ProductImageManagerComponent {
  private static nextId = 0;
  protected readonly api = inject(CatalogApi);
  private readonly i18n = inject(I18nService);
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  readonly productId = input.required<string>();
  readonly canManage = input(false);
  /** Show the "only the creating organization may change images" note even without images. */
  readonly readOnlyNote = input(false);
  /** Emits the current images after every successful change. */
  readonly changed = output<ProductImage[]>();

  protected readonly inputId = `hn-image-file-${ProductImageManagerComponent.nextId++}`;
  protected readonly accept = ACCEPTED_IMAGE_TYPES.join(',');
  protected readonly maxImages = MAX_IMAGES_PER_PRODUCT;

  protected readonly images = createQuery(
    () => this.productId(),
    (id) => this.api.images(id),
  );
  /** Optimistic order while a reorder request runs. */
  private readonly localOrder = signal<string[] | null>(null);
  protected readonly ordered = computed(() => {
    const list = [...(this.images.value() ?? [])].sort((a, b) => a.position - b.position);
    const order = this.localOrder();
    if (!order) return list;
    const byId = new Map(list.map((i) => [i.id, i]));
    return order.map((id) => byId.get(id)).filter((i): i is ProductImage => !!i);
  });
  protected readonly full = computed(() => this.ordered().length + this.pending().length >= MAX_IMAGES_PER_PRODUCT);

  protected readonly pending = signal<PendingUpload[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly announcement = signal('');
  protected readonly confirmDelete = signal<string | null>(null);
  protected readonly dragOver = signal<number | null>(null);
  protected readonly fileDragOver = signal(false);
  protected dragFrom: number | null = null;
  private nextPending = 1;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.pending().forEach((p) => p.previewUrl && URL.revokeObjectURL(p.previewUrl)));
  }

  /** Opens the file picker (used by the wizard right after a product was created). */
  pickFiles(): void {
    this.fileInput()?.nativeElement.click();
  }

  protected async onFiles(list: FileList | null): Promise<void> {
    if (!list || !this.canManage()) return;
    const files = Array.from(list);
    this.error.set(null);
    const room = MAX_IMAGES_PER_PRODUCT - this.ordered().length - this.pending().length;
    if (files.length > room) {
      this.error.set(this.i18n.t('images.tooMany', { max: MAX_IMAGES_PER_PRODUCT }));
    }
    for (const file of files.slice(0, Math.max(room, 0))) {
      const problem = checkImageFile(file);
      if (problem) {
        this.error.set(this.i18n.t(`images.file.${problem}`, { name: file.name }));
        continue;
      }
      await this.upload(file);
    }
  }

  private async upload(file: File): Promise<void> {
    const entry: PendingUpload = {
      id: this.nextPending++,
      name: file.name,
      previewUrl: typeof URL.createObjectURL === 'function' ? URL.createObjectURL(file) : null,
      error: null,
    };
    this.pending.update((p) => [...p, entry]);
    this.busy.set(true);
    try {
      const created = await firstValueFrom(this.api.uploadImage(this.productId(), file));
      this.images.set([...(this.images.value() ?? []), created]);
      this.announcement.set(this.i18n.t('images.uploaded', { name: file.name }));
      this.changed.emit(this.ordered());
    } catch (e) {
      this.error.set(`${file.name}: ${describeProblem(this.i18n, toApiProblem(e))}`);
    } finally {
      this.pending.update((p) => p.filter((x) => x.id !== entry.id));
      if (entry.previewUrl) URL.revokeObjectURL(entry.previewUrl);
      this.busy.set(false);
    }
  }

  protected async move(from: number, to: number): Promise<void> {
    const current = this.ordered().map((i) => i.id);
    const next = moveItem(current, from, to);
    if (sameOrder(current, next)) return;
    this.localOrder.set(next);
    this.busy.set(true);
    this.error.set(null);
    try {
      const saved = await firstValueFrom(this.api.reorderImages(this.productId(), next));
      this.images.set(saved);
      this.announcement.set(this.i18n.t('images.moved', { n: Math.min(Math.max(to, 0), next.length - 1) + 1 }));
      this.changed.emit(this.ordered().length ? [...saved].sort((a, b) => a.position - b.position) : []);
    } catch (e) {
      this.error.set(describeProblem(this.i18n, toApiProblem(e)));
      this.images.reload();
    } finally {
      this.localOrder.set(null);
      this.busy.set(false);
    }
  }

  protected async remove(img: ProductImage): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await firstValueFrom(this.api.deleteImage(this.productId(), img.id));
      const rest = (this.images.value() ?? []).filter((i) => i.id !== img.id);
      // Positions are compacted by the API; re-read to get the new primary image.
      this.images.set(rest);
      this.images.reload();
      this.announcement.set(this.i18n.t('images.deleted'));
      this.changed.emit(rest);
    } catch (e) {
      this.error.set(describeProblem(this.i18n, toApiProblem(e)));
    } finally {
      this.confirmDelete.set(null);
      this.busy.set(false);
    }
  }

  // ---- drag & drop: reorder images
  protected onDragStart(event: DragEvent, index: number): void {
    if (!this.canManage() || this.busy()) return;
    this.dragFrom = index;
    event.dataTransfer?.setData('text/plain', String(index));
    if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
  }

  protected onDragOver(event: DragEvent, index: number): void {
    if (this.dragFrom === null) return;
    event.preventDefault();
    this.dragOver.set(index);
  }

  protected onDrop(event: DragEvent, index: number): void {
    event.preventDefault();
    event.stopPropagation();
    const from = this.dragFrom;
    this.dragFrom = null;
    this.dragOver.set(null);
    if (from !== null && from !== index) void this.move(from, index);
  }

  // ---- drag & drop: files onto the upload area
  protected onFileDragOver(event: DragEvent): void {
    if (this.dragFrom !== null || !event.dataTransfer?.types.includes('Files')) return;
    event.preventDefault();
    this.fileDragOver.set(true);
  }

  protected onFileDrop(event: DragEvent): void {
    if (this.dragFrom !== null) return;
    event.preventDefault();
    this.fileDragOver.set(false);
    void this.onFiles(event.dataTransfer?.files ?? null);
  }
}
