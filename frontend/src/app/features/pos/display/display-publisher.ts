import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { StorageService } from '../../../core/storage.service';
import { DisplayApi } from './display-api';
import { DisplayState, displayChannelName, parseDisplayMessage } from './display-state';

/** How often the till repeats the state for a remote display (the API keeps it in memory only). */
const REMOTE_KEEPALIVE_MS = 30_000;
const REMOTE_DEBOUNCE_MS = 200;

interface Target {
  channel: BroadcastChannel | null;
  last: DisplayState | null;
  /** Remote link known to be active for this register (null = not asked yet). */
  remote: boolean | null;
  pushTimer: ReturnType<typeof setTimeout> | null;
  keepalive: ReturnType<typeof setInterval> | null;
  pushing: boolean;
  dirty: boolean;
}

/**
 * Publishes the till's display state: always on the BroadcastChannel of the register (local second screen, no backend)
 * and, while the register has a remote display link, to the API (debounced, latest state wins).
 * A display problem never affects selling: every failure here is swallowed and shown only as a status.
 */
@Injectable({ providedIn: 'root' })
export class DisplayPublisher {
  private readonly api = inject(DisplayApi);
  private readonly storage = inject(StorageService);
  private readonly targets = new Map<string, Target>();

  /** Last remote push failed (shown in the "Kundendisplay" dialog); null = fine or no remote display. */
  readonly remoteError = signal<unknown>(null);

  publish(registerId: string, state: DisplayState): void {
    if (!this.storage.isBrowser) return;
    const t = this.target(registerId);
    if (t.last && JSON.stringify(t.last) === JSON.stringify(state)) return;
    t.last = state;
    this.post(t, state);
    this.schedulePush(registerId, t);
  }

  /** The register has (or no longer has) a remote display link: start or stop pushing. */
  setRemote(registerId: string, active: boolean): void {
    if (!this.storage.isBrowser) return;
    const t = this.target(registerId);
    t.remote = active;
    if (!active) this.remoteError.set(null);
    this.syncKeepalive(registerId, t);
    if (active) this.schedulePush(registerId, t, 0);
  }

  /** The till page of this register is gone: stop the keep-alive (the last state stays on the display). */
  release(registerId: string): void {
    const t = this.targets.get(registerId);
    if (!t) return;
    if (t.pushTimer) clearTimeout(t.pushTimer);
    if (t.keepalive) clearInterval(t.keepalive);
    t.channel?.close();
    this.targets.delete(registerId);
  }

  private target(registerId: string): Target {
    let t = this.targets.get(registerId);
    if (t) return t;
    t = { channel: null, last: null, remote: null, pushTimer: null, keepalive: null, pushing: false, dirty: false };
    this.targets.set(registerId, t);
    if (typeof BroadcastChannel !== 'undefined') {
      try {
        const channel = new BroadcastChannel(displayChannelName(registerId));
        // A display that opens later asks for the current state.
        channel.onmessage = (e: MessageEvent) => {
          if (parseDisplayMessage(e.data)?.type === 'hello' && t!.last) this.post(t!, t!.last);
        };
        t.channel = channel;
      } catch {
        /* BroadcastChannel unavailable: the local display stays idle */
      }
    }
    // Ask once whether a remote link exists (a 404/403 or an older API simply means "no remote display").
    firstValueFrom(this.api.linkStatus(registerId))
      .then((s) => {
        if (t!.remote === null) this.setRemote(registerId, !!s?.active);
      })
      .catch(() => {
        if (t!.remote === null) t!.remote = false;
      });
    return t;
  }

  private post(t: Target, state: DisplayState): void {
    try {
      t.channel?.postMessage({ type: 'state', state });
    } catch {
      /* closed channel */
    }
  }

  private syncKeepalive(registerId: string, t: Target): void {
    if (t.remote && !t.keepalive) t.keepalive = setInterval(() => this.schedulePush(registerId, t, 0), REMOTE_KEEPALIVE_MS);
    if (!t.remote && t.keepalive) {
      clearInterval(t.keepalive);
      t.keepalive = null;
    }
  }

  private schedulePush(registerId: string, t: Target, delay = REMOTE_DEBOUNCE_MS): void {
    if (!t.remote || !t.last) return;
    if (t.pushTimer) clearTimeout(t.pushTimer);
    t.pushTimer = setTimeout(() => {
      t.pushTimer = null;
      void this.push(registerId, t);
    }, delay);
  }

  private async push(registerId: string, t: Target): Promise<void> {
    if (!t.remote || !t.last) return;
    if (t.pushing) {
      t.dirty = true;
      return;
    }
    t.pushing = true;
    try {
      await firstValueFrom(this.api.pushState(registerId, t.last));
      this.remoteError.set(null);
    } catch (e) {
      this.remoteError.set(e); // the keep-alive retries; selling is never blocked by the display
    } finally {
      t.pushing = false;
      if (t.dirty) {
        t.dirty = false;
        this.schedulePush(registerId, t, 0);
      }
    }
  }
}
