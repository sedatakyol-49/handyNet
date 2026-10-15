import { Injectable, inject, signal } from '@angular/core';
import { ApiProblem, toApiProblem } from '../api/api-problem';
import { I18nService } from '../i18n/i18n.service';

export type ToastKind = 'success' | 'error' | 'info';

export interface Toast {
  id: number;
  kind: ToastKind;
  text: string;
}

@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly i18n = inject(I18nService);
  private nextId = 1;
  readonly toasts = signal<Toast[]>([]);

  success(text: string): void {
    this.push('success', text);
  }

  info(text: string): void {
    this.push('info', text);
  }

  error(errorOrText: unknown): void {
    this.push('error', typeof errorOrText === 'string' ? errorOrText : describeProblem(this.i18n, toApiProblem(errorOrText)));
  }

  dismiss(id: number): void {
    this.toasts.update((list) => list.filter((t) => t.id !== id));
  }

  private push(kind: ToastKind, text: string): void {
    const id = this.nextId++;
    this.toasts.update((list) => [...list.slice(-4), { id, kind, text }]);
    if (typeof setTimeout !== 'undefined') setTimeout(() => this.dismiss(id), kind === 'error' ? 8000 : 4000);
  }
}

/**
 * Human-readable text for an API problem: a translated message for known codes, otherwise the
 * API's own `title` (which is the authoritative explanation, e.g. for 409 conflicts).
 */
export function describeProblem(i18n: I18nService, problem: ApiProblem): string {
  if (problem.status === 0) return i18n.t('errors.network');
  if (problem.code && i18n.has(`errors.codes.${problem.code}`)) return i18n.t(`errors.codes.${problem.code}`);
  if (problem.title) return problem.title;
  if (problem.status === 403) return i18n.t('errors.forbidden');
  if (problem.status === 404) return i18n.t('errors.notFound');
  return i18n.t('errors.unexpected');
}
