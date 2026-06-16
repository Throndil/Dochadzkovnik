import { Injectable, signal } from '@angular/core';

export type ToastType = 'success' | 'error' | 'info';

export interface Toast {
  id: number;
  message: string;
  type: ToastType;
}

/**
 * Lightweight global toast/snackbar. A single <app-toast /> at the app root
 * renders whatever is in `toasts`. Call success()/error()/info() from anywhere.
 * Toasts auto-dismiss; errors linger a little longer so they aren't missed.
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);
  private nextId = 1;

  success(message: string) { this.show(message, 'success'); }
  error(message: string)   { this.show(message, 'error', 5000); }
  info(message: string)    { this.show(message, 'info'); }

  show(message: string, type: ToastType = 'success', durationMs = 3000) {
    const id = this.nextId++;
    this.toasts.update(list => [...list, { id, message, type }]);
    setTimeout(() => this.dismiss(id), durationMs);
  }

  dismiss(id: number) {
    this.toasts.update(list => list.filter(t => t.id !== id));
  }
}
