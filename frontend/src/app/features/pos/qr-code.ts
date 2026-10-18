import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { qrSvgPath } from './fiscal.logic';

/** QR code as inline SVG, generated in the browser (qrcode-generator, MIT); prints crisply on receipt printers. */
@Component({
  selector: 'hn-qr-code',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'inline-block' },
  template: `
    @if (qr(); as q) {
      <svg [attr.viewBox]="'-2 -2 ' + (q.size + 4) + ' ' + (q.size + 4)" [attr.width]="sizePx()" [attr.height]="sizePx()" role="img"
        [attr.aria-label]="label()" shape-rendering="crispEdges" data-testid="qr-code">
        <rect x="-2" y="-2" [attr.width]="q.size + 4" [attr.height]="q.size + 4" fill="#fff" />
        <path [attr.d]="q.path" fill="#000" />
      </svg>
    }
  `,
})
export class QrCodeComponent {
  readonly data = input.required<string>();
  readonly label = input('QR');
  readonly sizePx = input(144);
  protected readonly qr = computed(() => {
    try {
      return qrSvgPath(this.data());
    } catch {
      return null; // payload too long for a QR code: the text fields are printed anyway
    }
  });
}
