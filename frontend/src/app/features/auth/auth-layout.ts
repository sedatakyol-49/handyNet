import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { I18nService, Lang } from '../../core/i18n/i18n.service';

/** Centered card layout for anonymous pages with a language switcher. */
@Component({
  selector: 'hn-auth-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex min-h-screen flex-col">
      <header class="flex items-center justify-between px-4 py-3">
        <span class="flex items-center gap-2 font-semibold">
          <span class="grid size-7 place-items-center rounded bg-slate-900 text-xs font-bold text-white" aria-hidden="true">HN</span>
          HandyNet
        </span>
        <label class="sr-only" for="auth-lang">{{ i18n.t('shell.language') }}</label>
        <select id="auth-lang" class="hn-control w-auto py-1" [value]="i18n.lang()" (change)="use($any($event.target).value)">
          @for (l of i18n.languages; track l.code) {
            <option [value]="l.code" [selected]="l.code === i18n.lang()">{{ l.label }}</option>
          }
        </select>
      </header>
      <main class="flex flex-1 items-start justify-center px-4 py-8 sm:items-center">
        <div class="w-full" [class]="wide() ? 'max-w-3xl' : 'max-w-sm'">
          <ng-content />
        </div>
      </main>
      <footer class="px-4 py-3 text-center text-xs text-slate-500">{{ i18n.t('auth.footer') }}</footer>
    </div>
  `,
})
export class AuthLayoutComponent {
  protected readonly i18n = inject(I18nService);
  readonly wide = input(false);

  protected use(lang: string): void {
    void this.i18n.use(lang as Lang);
  }
}
