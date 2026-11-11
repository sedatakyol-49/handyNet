import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, effect, inject, input, untracked } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { SessionStore } from '../../core/auth/session.store';
import { TranslatePipe } from '../../core/i18n/translate.pipe';
import { createQuery } from '../../shared/data/query';
import { PosContext } from './pos-context';
import { PosOfflineData } from './offline/offline-data.service';
import { OfflineQueueService } from './offline/offline-queue.service';

/**
 * Tabs of one register on the POS app's secondary pages (selling, receipts, sessions). Also tells the top bar which
 * register and session the page belongs to.
 */
@Component({
  selector: 'hn-pos-subnav',
  imports: [RouterLink, RouterLinkActive, TranslatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'hn-no-print mb-4 block' },
  template: `
    <nav class="hn-pos-scroll-x flex flex-nowrap items-center gap-1 border-b border-pos-line text-sm" [attr.aria-label]="'pos.subnav.label' | t" data-testid="pos-subnav">
      @for (tab of visibleTabs(); track tab.id) {
        <a [routerLink]="['/kasse', registerId(), ...tab.path]" routerLinkActive="border-pos-accent text-pos-accent-text font-semibold"
          [routerLinkActiveOptions]="{ exact: tab.id === 'till' }" #rla="routerLinkActive" [attr.aria-current]="rla.isActive ? 'page' : null"
          class="inline-flex min-h-11 shrink-0 items-center border-b-[3px] border-transparent px-3.5 font-medium whitespace-nowrap text-pos-muted hover:text-pos-ink" [attr.data-testid]="'pos-tab-' + tab.id">
          {{ tab.label | t }}
        </a>
      }
    </nav>
  `,
})
export class PosSubnavComponent {
  private readonly ctx = inject(PosContext);

  readonly registerId = input.required<string>();
  private readonly store = inject(SessionStore);
  private readonly tabs = [
    { id: 'till', path: [] as string[], label: 'pos.subnav.till', manage: false },
    { id: 'receipts', path: ['belege'], label: 'pos.subnav.receipts', manage: false },
    { id: 'sessions', path: ['sitzungen'], label: 'pos.subnav.sessions', manage: false },
    { id: 'quotations', path: ['angebote'], label: 'pos.subnav.quotations', manage: false },
    { id: 'invoices', path: ['rechnungen'], label: 'pos.subnav.invoices', manage: false },
    { id: 'prices', path: ['preise'], label: 'pos.subnav.prices', manage: true },
    { id: 'ankauf', path: ['ankauf'], label: 'pos.subnav.ankauf', manage: false, module: 'Ankauf' as const },
    { id: 'printers', path: ['drucker'], label: 'pos.subnav.printers', manage: false },
    { id: 'terminals', path: ['kartenterminal'], label: 'pos.subnav.terminals', manage: false },
    { id: 'offline', path: ['offline'], label: 'pos.subnav.offline', manage: false, offline: true },
  ];
  private readonly offlineData = inject(PosOfflineData);
  private readonly queue = inject(OfflineQueueService);
  /** The Offline tab exists for registers in offline mode and whenever this device still holds entries of the register. */
  private readonly showOffline = computed(() => !!this.register.value()?.offlineMode || this.queue.entries().some((e) => e.registerId === this.registerId()));
  /** Price maintenance only with `pos.manage`; Ankauf only with the module. */
  protected readonly visibleTabs = computed(() =>
    this.tabs.filter(
      (t) => (!t.manage || this.store.can('pos.manage')) && (!('module' in t) || !t.module || this.store.hasModule(t.module)) && (!('offline' in t) || this.showOffline()),
    ),
  );

  private readonly register = createQuery(
    () => this.registerId(),
    (id) => this.offlineData.register(id),
  );
  private readonly session = createQuery(
    () => this.registerId(),
    (id) => this.offlineData.currentSession(id),
  );
  private readonly sessionLoaded = computed(() => !this.session.loading());

  constructor() {
    // Narrow screens: the tab strip scrolls sideways; keep the current tab in view.
    const host = inject<ElementRef<HTMLElement>>(ElementRef);
    afterNextRender(() => host.nativeElement.querySelector<HTMLElement>('[aria-current="page"]')?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' }));
    effect(() => {
      const id = this.registerId();
      const name = this.register.value()?.name ?? null;
      untracked(() => this.ctx.setRegister(id, name));
    });
    effect(() => {
      const s = this.session.value();
      const loaded = this.sessionLoaded();
      untracked(() => this.ctx.session.set(loaded || s ? s : undefined));
    });
  }

  /** After closing a session or other changes. */
  reloadSession(): void {
    this.session.reload();
  }
}
