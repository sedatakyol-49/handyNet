import { ChangeDetectionStrategy, Component, computed, effect, inject, linkedSignal, signal, untracked } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService, Lang } from '../../core/i18n/i18n.service';
import { TranslatePipe, TranslateEnumPipe } from '../../core/i18n/translate.pipe';
import { CartStore } from '../cart/cart.store';
import { EmailVerificationBannerComponent } from './email-verification-banner';
import { NAV_ITEMS, NAV_SECTIONS, NavItem } from './navigation';

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

  constructor() {
    // Signed out in another tab (or the session ended): leave the app. A different user signed in elsewhere: reload /api/me.
    effect(() => {
      if (this.session.signedOutElsewhere() > 0) void untracked(() => this.router.navigate(['/login']));
    });
    effect(() => {
      if (this.session.isAuthenticated() && !this.session.me()) void untracked(() => this.auth.loadMe());
    });

    // Close the mobile drawer after navigation.
    this.router.events
      .pipe(
        filter((e) => e instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        this.navOpen.set(false);
        this.userMenuOpen.set(false);
      });
  }

  protected itemsOf(section: NavItem['section']): NavItem[] {
    return this.visibleItems().filter((i) => i.section === section);
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
