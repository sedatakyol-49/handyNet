import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { toApiProblem } from '../../core/api/api-problem';
import { emailLanguage } from '../../core/auth/account-links';
import { AuthService } from '../../core/auth/auth.service';
import { SessionStore } from '../../core/auth/session.store';
import { I18nService } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { UI } from '../../shared/ui';

/** The API allows one verification email per minute (409 auth.verification_recently_sent). */
const RESEND_COOLDOWN_MS = 60_000;
const DISMISS_KEY = 'hn.verifyBannerDismissed';

/**
 * "Please confirm your email address" with "resend". In the app shell it can be dismissed for the browser session
 * (sessionStorage, per user); `inline` (account page, verify landing page) is never dismissible.
 * Shown only while `/api/me` says `emailVerified: false`.
 */
@Component({
  selector: 'hn-email-verification-banner',
  imports: [...UI],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block' },
  template: `
    @if (visible()) {
      <hn-banner
        tone="warning"
        [heading]="'account.verify.bannerTitle' | t"
        [text]="'account.verify.bannerText' | t: { email: session.me()?.email }"
        [dismissible]="!inline()"
        (dismissed)="dismiss()"
        data-testid="verify-email-banner"
      >
        <div class="mt-2 flex flex-wrap items-center gap-3">
          <button hnButton size="sm" type="button" [loading]="busy()" [disabled]="busy() || coolingDown()" (click)="resend()" data-testid="verify-resend">
            {{ 'account.verify.resend' | t }}
          </button>
          @if (notice(); as n) {
            <span class="text-sm" role="status">{{ n }}</span>
          }
        </div>
      </hn-banner>
    }
  `,
})
export class EmailVerificationBannerComponent {
  protected readonly session = inject(SessionStore);
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);

  readonly inline = input(false);

  protected readonly busy = signal(false);
  protected readonly coolingDown = signal(false);
  private readonly noticeKey = signal<{ key: string } | { text: string } | null>(null);
  private readonly dismissedFor = signal<string | null>(readDismissed());
  private timer: ReturnType<typeof setTimeout> | null = null;

  protected readonly notice = computed(() => {
    const n = this.noticeKey();
    if (!n) return null;
    return 'key' in n ? this.i18n.t(n.key, { email: this.session.me()?.email }) : n.text;
  });

  protected readonly visible = computed(() => {
    const me = this.session.me();
    if (!me || me.emailVerified !== false) return false;
    return this.inline() || this.dismissedFor() !== me.userId;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.timer && clearTimeout(this.timer));
  }

  protected dismiss(): void {
    const userId = this.session.me()?.userId ?? null;
    this.dismissedFor.set(userId);
    try {
      if (userId) sessionStorage.setItem(DISMISS_KEY, userId);
    } catch {
      /* storage unavailable: dismissed for this page view only */
    }
  }

  protected async resend(): Promise<void> {
    if (this.busy() || this.coolingDown()) return;
    this.busy.set(true);
    this.noticeKey.set(null);
    try {
      await this.auth.resendVerification(emailLanguage(this.i18n.lang()));
      this.noticeKey.set({ key: 'account.verify.sent' });
      this.startCooldown();
    } catch (e) {
      const problem = toApiProblem(e);
      if (problem.code === 'auth.verification_recently_sent') {
        this.noticeKey.set({ key: 'errors.codes.auth.verification_recently_sent' });
        this.startCooldown();
      } else if (problem.code === 'auth.email_already_verified') {
        // Verified meanwhile (e.g. in another tab): reload /api/me, the banner disappears.
        await this.auth.loadMe(true);
      } else {
        this.noticeKey.set({ text: describeProblem(this.i18n, problem) });
      }
    } finally {
      this.busy.set(false);
    }
  }

  private startCooldown(): void {
    this.coolingDown.set(true);
    if (this.timer) clearTimeout(this.timer);
    this.timer = setTimeout(() => this.coolingDown.set(false), RESEND_COOLDOWN_MS);
  }
}

function readDismissed(): string | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage.getItem(DISMISS_KEY);
  } catch {
    return null;
  }
}
