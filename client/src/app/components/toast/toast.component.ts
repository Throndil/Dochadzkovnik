import { Component, inject } from '@angular/core';
import { NgClass } from '@angular/common';
import { ToastService } from '../../services/toast.service';

/**
 * Renders the global toast stack at the bottom-center of the screen.
 * Mounted once via <app-toast /> in app.html. Reads ToastService.toasts.
 */
@Component({
  selector: 'app-toast',
  standalone: true,
  imports: [NgClass],
  template: `
    <div class="fixed bottom-4 inset-x-0 z-[1000] flex flex-col items-center gap-2 px-4 pointer-events-none">
      @for (t of toastService.toasts(); track t.id) {
        <div role="status"
             class="toast-item pointer-events-auto w-full max-w-sm flex items-center gap-3 px-4 py-3 rounded-xl shadow-lg border text-sm font-medium"
             [ngClass]="classFor(t.type)">
          <span class="shrink-0">
            @switch (t.type) {
              @case ('success') {
                <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                  <path stroke-linecap="round" stroke-linejoin="round" d="M5 13l4 4L19 7"/>
                </svg>
              }
              @case ('error') {
                <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                  <path stroke-linecap="round" stroke-linejoin="round" d="M12 9v4m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z"/>
                </svg>
              }
              @default {
                <svg class="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                  <path stroke-linecap="round" stroke-linejoin="round" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/>
                </svg>
              }
            }
          </span>
          <span class="flex-1">{{ t.message }}</span>
          <button type="button" (click)="toastService.dismiss(t.id)"
                  class="shrink-0 opacity-60 hover:opacity-100 transition-opacity" aria-label="Zavrieť">
            <svg class="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.5">
              <path stroke-linecap="round" stroke-linejoin="round" d="M6 18L18 6M6 6l12 12"/>
            </svg>
          </button>
        </div>
      }
    </div>
  `,
  styles: [`
    .toast-item {
      animation: toast-in 0.22s ease-out;
    }
    @keyframes toast-in {
      from { opacity: 0; transform: translateY(12px); }
      to   { opacity: 1; transform: translateY(0); }
    }
  `]
})
export class ToastComponent {
  protected toastService = inject(ToastService);

  protected classFor(type: string): string {
    switch (type) {
      case 'success':
        return 'bg-green-50 dark:bg-green-950 border-green-300 dark:border-green-700 text-green-800 dark:text-green-200';
      case 'error':
        return 'bg-red-50 dark:bg-red-950 border-red-300 dark:border-red-700 text-red-800 dark:text-red-200';
      default:
        return 'bg-slate-50 dark:bg-slate-800 border-slate-300 dark:border-slate-600 text-slate-800 dark:text-slate-100';
    }
  }
}
