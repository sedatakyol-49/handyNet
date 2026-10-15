import { DestroyRef, Signal, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Observable, Subject, catchError, merge, map, of, switchMap, tap } from 'rxjs';

export interface Query<T> {
  /** Last successfully loaded value; kept while a new request is in flight (no table flicker). */
  readonly value: Signal<T | null>;
  readonly loading: Signal<boolean>;
  readonly error: Signal<unknown>;
  reload(): void;
  /** Replace the local value, e.g. after a successful mutation returned the updated entity. */
  set(value: T | null): void;
}

/**
 * Loads data whenever the reactive `params` change (latest request wins). Must be created in an
 * injection context (field initializer of a component/service).
 */
export function createQuery<P, T>(params: () => P, fetch: (params: P) => Observable<T>): Query<T> {
  const value = signal<T | null>(null);
  const loading = signal(false);
  const error = signal<unknown>(null);
  const reload$ = new Subject<void>();
  const params$ = toObservable(computed(params));
  let latest: P | undefined;

  merge(params$.pipe(tap((p) => (latest = p))), reload$.pipe(map(() => latest as P)))
    .pipe(
      tap(() => {
        loading.set(true);
        error.set(null);
      }),
      switchMap((p) =>
        fetch(p).pipe(
          map((v) => ({ ok: true as const, v })),
          catchError((e: unknown) => of({ ok: false as const, e })),
        ),
      ),
      takeUntilDestroyed(inject(DestroyRef)),
    )
    .subscribe((r) => {
      loading.set(false);
      if (r.ok) value.set(r.v);
      else error.set(r.e);
    });

  return {
    value: value.asReadonly(),
    loading: loading.asReadonly(),
    error: error.asReadonly(),
    reload: () => reload$.next(),
    set: (v) => value.set(v),
  };
}
