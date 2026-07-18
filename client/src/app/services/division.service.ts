import { Injectable, computed, signal } from '@angular/core';

export type Division = 'profistav' | 'stroje';

export const DIVISION_LABELS: Record<Division, string> = {
  profistav: 'AZ Profistav',
  stroje: 'AZ Stroje'
};

/**
 * Active company division (Fáza D). The "burger" in the Financie bar switches
 * it; the invoices page, uploads and (later) Mzdy read it. Persisted per
 * device so the manager lands back in the division they worked in.
 */
@Injectable({ providedIn: 'root' })
export class DivisionService {
  private readonly STORAGE_KEY = 'division';

  active = signal<Division>(this.load());
  label = computed(() => DIVISION_LABELS[this.active()]);

  set(d: Division) {
    this.active.set(d);
    try { localStorage.setItem(this.STORAGE_KEY, d); } catch { /* ignore */ }
  }

  private load(): Division {
    try {
      return localStorage.getItem(this.STORAGE_KEY) === 'stroje' ? 'stroje' : 'profistav';
    } catch {
      return 'profistav';
    }
  }
}
