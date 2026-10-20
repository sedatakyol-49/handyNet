import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { StorageService } from '../../core/storage.service';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService, Lang } from '../../core/i18n/i18n.service';
import { TranslatePipe, TranslateEnumPipe } from '../../core/i18n/translate.pipe';
import { CartStore } from '../cart/cart.store';
import { EmailVerificationBannerComponent } from './email-verification-banner';
import { NAV_ITEMS, NAV_SECTIONS, NavItem } from './navigation';

const NAV_COLLAPSED_KEY = 'hn.nav.collapsed';

@Component({
  selector: 'hn-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslatePipe, TranslateEnumPipe, EmailVerificationBannerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
})
export class ShellComponent {
  protected readonly session = inject(SessionStore);
  protected readonly i18n = inject(I18nService);
  protected readonly cart = inject(CartStore);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly storage = inject(StorageService);

  protected readonly navOpen = signal(false);
  protected readonly userMenuOpen = signal(false);
  protected readonly sections = NAV_SECTIONS;

  /** Re-creates the routed page when the organization changes so every page reloads its data. */
  protected readonly outletKey = linkedSignal<string | null, string[]>({
    source: () => this.session.organizationId(),
    // Keep the current page while logging out (org becomes null) instead of re-creating it without a token.
    computation: (org, previous) => (org ? [org] : (previous?.value ?? ['none'])),
  });

  protected readonly visibleItems = computed(() => NAV_ITEMS.filter((item) => this.isVisible(item)));

  /** Collapsed side-navigation categories, remembered per browser. */
  private readonly collapsedSections = signal<ReadonlySet<string>>(
    new Set(this.storage.getJson<string[]>(NAV_COLLAPSED_KEY)?.filter((s) => typeof s === 'string') ?? []),
  );

  constructor() {
    // Close the mobile drawer after navigation; open the category of the page you navigated to.
    this.router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((e) => {
        this.navOpen.set(false);
        this.userMenuOpen.set(false);
        this.expandSectionOf((e as NavigationEnd).urlAfterRedirects);
      });
    this.expandSectionOf(this.router.url);
  }

  protected itemsOf(section: NavItem['section']): NavItem[] {
    return this.visibleItems().filter((i) => i.section === section);
  }

  protected isCollapsed(section: NavItem['section']): boolean {
    return this.collapsedSections().has(section);
  }

  protected toggleSection(section: NavItem['section']): void {
    const next = new Set(this.collapsedSections());
    if (!next.delete(section)) next.add(section);
    this.setCollapsed(next);
  }

  private expandSectionOf(url: string): void {
    const path = url.split(/[?#]/)[0];
    const item = NAV_ITEMS.find((i) => (i.exact ? path === i.path : path === i.path || path.startsWith(i.path + '/')));
    if (!item || !this.collapsedSections().has(item.section)) return;
    const next = new Set(this.collapsedSections());
    next.delete(item.section);
    this.setCollapsed(next);
  }

  private setCollapsed(next: ReadonlySet<string>): void {
    this.collapsedSections.set(next);
    this.storage.setJson(NAV_COLLAPSED_KEY, [...next]);
  }

  protected switchOrganization(id: string): void {
    this.session.selectOrganization(id);
    void this.router.navigateByUrl('/app/uebersicht');
  }

  protected setLanguage(lang: string): void {
    void this.i18n.use(lang as Lang);
  }

  protected logout(): void {
    this.userMenuOpen.set(false);
    this.auth.logout();
  }

  private isVisible(item: NavItem): boolean {
    if (item.adminOnly) return this.session.isPlatformAdmin();
    if (item.always && !item.permission) return true;
    if (!this.session.membership()) return false;
    if (item.always) return !item.permission || this.session.can(item.permission);
    return this.session.allows(item.module, item.permission);
  }
}
