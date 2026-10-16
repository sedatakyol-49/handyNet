import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { I18nService, Lang } from '../../core/i18n/i18n.service';

/**
 * Layout for anonymous pages: a brand showcase panel (large screens) next to the content column with a language
 * switcher. The showcase is pure HTML/CSS (no external images), uses logical properties so it mirrors in RTL and only
 * animates when the user has not asked for reduced motion.
 */
@Component({
  selector: 'hn-auth-layout',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="flex min-h-screen bg-slate-50">
      <!-- Brand showcase -->
      <aside
        class="hn-auth-hero relative isolate hidden shrink-0 flex-col overflow-hidden bg-slate-950 text-white"
        [class]="wide() ? 'xl:flex xl:w-[40%]' : 'lg:flex lg:w-[46%] xl:w-1/2'"
        aria-hidden="true"
      >
        <div class="hn-auth-glow hn-auth-glow-a"></div>
        <div class="hn-auth-glow hn-auth-glow-b"></div>
        <div class="hn-auth-grid absolute inset-0 -z-10"></div>

        <div class="flex items-center gap-2.5 px-10 pt-9 text-lg font-semibold tracking-tight">
          <span class="grid size-9 place-items-center rounded-xl bg-white/10 text-sm font-bold ring-1 ring-white/20 backdrop-blur">HN</span>
          HandyNet
        </div>

        <div class="flex flex-1 flex-col justify-center px-10 xl:px-14">
          <h2 class="max-w-md text-3xl leading-tight font-semibold tracking-tight text-white xl:text-4xl">{{ i18n.t('auth.hero.title') }}</h2>
          <p class="mt-4 max-w-md text-base leading-relaxed text-slate-300">{{ i18n.t('auth.hero.subtitle') }}</p>

          <!-- Device illustration with floating cards -->
          <div class="relative mt-10 h-[340px] max-w-lg">
            <div class="hn-auth-phone absolute inset-y-0 start-[18%] w-[170px] rounded-[2rem] bg-slate-900 p-2 shadow-2xl ring-1 ring-white/15">
              <div class="flex h-full flex-col gap-2 rounded-[1.6rem] bg-gradient-to-b from-slate-800 to-slate-900 p-3">
                <div class="mx-auto mb-1 h-1.5 w-12 rounded-full bg-white/15"></div>
                <div class="h-2 w-16 rounded bg-white/30"></div>
                <div class="flex h-20 items-end gap-1.5 rounded-xl bg-white/5 p-2">
                  @for (h of bars; track $index) {
                    <span class="flex-1 rounded-sm bg-gradient-to-t from-cyan-400 to-indigo-400" [style.height.%]="h"></span>
                  }
                </div>
                @for (row of [0, 1, 2]; track row) {
                  <div class="flex items-center gap-2 rounded-lg bg-white/5 p-1.5">
                    <span class="size-5 rounded-md bg-indigo-400/40"></span>
                    <span class="h-1.5 flex-1 rounded bg-white/20"></span>
                    <span class="h-1.5 w-6 rounded bg-emerald-300/60"></span>
                  </div>
                }
              </div>
            </div>

            <div class="hn-auth-card hn-auth-float-1 absolute start-0 top-6 w-56">
              <div class="flex items-center gap-2.5">
                <span class="grid size-8 place-items-center rounded-lg bg-emerald-400/20 text-emerald-300">
                  <svg viewBox="0 0 20 20" fill="currentColor" class="size-4"><path fill-rule="evenodd" d="M16.7 5.3a1 1 0 0 1 0 1.4l-7.5 7.5a1 1 0 0 1-1.4 0L3.3 9.7a1 1 0 1 1 1.4-1.4l3.8 3.8 6.8-6.8a1 1 0 0 1 1.4 0Z" clip-rule="evenodd"/></svg>
                </span>
                <div class="min-w-0">
                  <p class="text-xs text-slate-300">{{ i18n.t('auth.hero.cardImei') }}</p>
                  <p class="font-mono text-sm text-white">35 209 811 4402 17</p>
                </div>
              </div>
            </div>

            <div class="hn-auth-card hn-auth-float-2 absolute end-0 top-28 w-52">
              <p class="text-xs text-slate-300">{{ i18n.t('auth.hero.cardReceipt') }}</p>
              <p class="mt-0.5 text-lg font-semibold tabular-nums text-white">249,00 €</p>
              <div class="mt-2 flex gap-1">
                <span class="h-1 flex-1 rounded bg-cyan-300/70"></span>
                <span class="h-1 flex-1 rounded bg-indigo-300/70"></span>
                <span class="h-1 w-6 rounded bg-white/20"></span>
              </div>
            </div>

            <div class="hn-auth-card hn-auth-float-3 absolute start-[6%] bottom-2 w-60">
              <div class="flex items-center justify-between gap-3">
                <div>
                  <p class="text-xs text-slate-300">{{ i18n.t('auth.hero.cardOffer') }}</p>
                  <p class="text-sm font-medium text-white">iPhone 14 · 128 GB</p>
                </div>
                <span class="rounded-md bg-indigo-400/20 px-2 py-1 text-sm font-semibold tabular-nums text-indigo-200">312 €</span>
              </div>
            </div>
          </div>

          <ul class="mt-10 grid max-w-lg gap-3 text-sm text-slate-200">
            @for (f of features; track f) {
              <li class="flex items-start gap-3">
                <span class="mt-0.5 grid size-5 shrink-0 place-items-center rounded-full bg-cyan-400/15 text-cyan-300">
                  <svg viewBox="0 0 20 20" fill="currentColor" class="size-3.5"><path fill-rule="evenodd" d="M16.7 5.3a1 1 0 0 1 0 1.4l-7.5 7.5a1 1 0 0 1-1.4 0L3.3 9.7a1 1 0 1 1 1.4-1.4l3.8 3.8 6.8-6.8a1 1 0 0 1 1.4 0Z" clip-rule="evenodd"/></svg>
                </span>
                {{ i18n.t(f) }}
              </li>
            }
          </ul>
        </div>

        <p class="px-10 pb-8 text-xs text-slate-400">{{ i18n.t('auth.footer') }}</p>
      </aside>

      <!-- Content -->
      <div class="hn-auth-content relative flex min-w-0 flex-1 flex-col">
        <header class="flex items-center justify-between px-4 py-3 sm:px-8 sm:py-5">
          <span class="flex items-center gap-2 font-semibold" [class]="wide() ? 'xl:invisible' : 'lg:invisible'">
            <span class="grid size-8 place-items-center rounded-lg bg-gradient-to-br from-indigo-600 to-cyan-500 text-xs font-bold text-white shadow-sm" aria-hidden="true">HN</span>
            HandyNet
          </span>
          <label class="sr-only" for="auth-lang">{{ i18n.t('shell.language') }}</label>
          <select id="auth-lang" class="hn-control w-auto py-1" [value]="i18n.lang()" (change)="use($any($event.target).value)">
            @for (l of i18n.languages; track l.code) {
              <option [value]="l.code" [selected]="l.code === i18n.lang()">{{ l.label }}</option>
            }
          </select>
        </header>
        <main class="flex flex-1 items-start justify-center px-4 py-8 sm:items-center sm:px-8">
          <div class="w-full" [class]="wide() ? 'max-w-3xl' : 'max-w-sm'">
            <ng-content />
          </div>
        </main>
        <footer class="px-4 py-3 text-center text-xs text-slate-500" [class]="wide() ? 'xl:hidden' : 'lg:hidden'">{{ i18n.t('auth.footer') }}</footer>
      </div>
    </div>
  `,
  styles: `
    .hn-auth-grid {
      background-image:
        linear-gradient(to right, rgb(255 255 255 / 0.05) 1px, transparent 1px),
        linear-gradient(to bottom, rgb(255 255 255 / 0.05) 1px, transparent 1px);
      background-size: 44px 44px;
      mask-image: radial-gradient(ellipse at 40% 45%, black 20%, transparent 75%);
    }
    .hn-auth-glow {
      position: absolute;
      z-index: -10;
      border-radius: 9999px;
      filter: blur(90px);
      opacity: 0.55;
    }
    .hn-auth-glow-a {
      inset-inline-start: -10%;
      top: 8%;
      width: 60%;
      height: 45%;
      background: radial-gradient(circle, #4f46e5, transparent 70%);
    }
    .hn-auth-glow-b {
      inset-inline-end: -15%;
      bottom: 5%;
      width: 65%;
      height: 50%;
      background: radial-gradient(circle, #06b6d4, transparent 70%);
    }
    .hn-auth-card {
      border-radius: 1rem;
      padding: 0.85rem 1rem;
      background: rgb(255 255 255 / 0.08);
      border: 1px solid rgb(255 255 255 / 0.14);
      box-shadow: 0 20px 40px -20px rgb(0 0 0 / 0.6);
      backdrop-filter: blur(14px);
    }
    .hn-auth-content {
      background:
        radial-gradient(60rem 30rem at 100% 0%, rgb(79 70 229 / 0.07), transparent 60%),
        radial-gradient(40rem 25rem at 0% 100%, rgb(6 182 212 / 0.06), transparent 60%);
    }
    @media (prefers-reduced-motion: no-preference) {
      .hn-auth-float-1 { animation: hn-float 7s ease-in-out infinite; }
      .hn-auth-float-2 { animation: hn-float 8s ease-in-out -2s infinite; }
      .hn-auth-float-3 { animation: hn-float 9s ease-in-out -4s infinite; }
      .hn-auth-phone { animation: hn-float 10s ease-in-out -1s infinite; }
    }
    @keyframes hn-float {
      0%, 100% { transform: translateY(0); }
      50% { transform: translateY(-8px); }
    }
  `,
})
export class AuthLayoutComponent {
  protected readonly i18n = inject(I18nService);
  readonly wide = input(false);

  protected readonly bars = [38, 62, 48, 80, 66, 92, 74];
  protected readonly features = ['auth.hero.feature1', 'auth.hero.feature2', 'auth.hero.feature3'];

  protected use(lang: string): void {
    void this.i18n.use(lang as Lang);
  }
}
