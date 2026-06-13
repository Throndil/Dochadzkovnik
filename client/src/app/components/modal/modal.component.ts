import { Component, computed, input, output } from '@angular/core';
import { CommonModule } from '@angular/common';

/**
 * Reusable confirm / info / success modal.
 *
 * Three variants via the `variant` input:
 *   - 'confirm'  → two buttons (cancel + confirm), used to replace window.confirm()
 *   - 'danger'   → same shape as confirm but red-tinted, for destructive actions
 *   - 'success'  → single OK button, used for "Úspešne nahrané" etc.
 *
 * Usage (parent owns the open/close state via a signal):
 *   <app-modal
 *     [open]="confirmDelete()"
 *     variant="danger"
 *     title="Odstrániť faktúru?"
 *     message="Toto je nevratná akcia..."
 *     confirmLabel="Áno, odstrániť"
 *     (confirm)="doDelete()"
 *     (cancel)="confirmDelete.set(false)" />
 */
@Component({
  selector: 'app-modal',
  standalone: true,
  imports: [CommonModule],
  template: `
    @if (open()) {
      <div class="fixed inset-0 z-50 flex items-center justify-center p-4"
           role="dialog" aria-modal="true"
           (click)="onBackdrop($event)">
        <!-- Backdrop -->
        <div class="absolute inset-0 bg-slate-900/60 backdrop-blur-sm"></div>

        <!-- Card -->
        <div class="relative bg-white dark:bg-slate-800 rounded-2xl shadow-xl border border-slate-200 dark:border-slate-700 max-w-md w-full p-6 space-y-4"
             (click)="$event.stopPropagation()">
          <!-- Icon + title row -->
          <div class="flex items-start gap-3">
            <div [class]="iconWrapClass()"
                 class="flex-shrink-0 rounded-full w-10 h-10 flex items-center justify-center">
              @if (variant() === 'success') {
                <!-- Check -->
                <svg xmlns="http://www.w3.org/2000/svg" class="w-6 h-6" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                  <polyline points="20 6 9 17 4 12"/>
                </svg>
              } @else if (variant() === 'danger') {
                <!-- Triangle warning -->
                <svg xmlns="http://www.w3.org/2000/svg" class="w-6 h-6" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0Z"/>
                  <line x1="12" y1="9" x2="12" y2="13"/>
                  <line x1="12" y1="17" x2="12.01" y2="17"/>
                </svg>
              } @else {
                <!-- Question -->
                <svg xmlns="http://www.w3.org/2000/svg" class="w-6 h-6" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <circle cx="12" cy="12" r="10"/>
                  <path d="M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3"/>
                  <line x1="12" y1="17" x2="12.01" y2="17"/>
                </svg>
              }
            </div>
            <div class="flex-1 min-w-0">
              <h3 class="text-lg font-semibold text-slate-900 dark:text-white">{{ title() }}</h3>
              @if (message()) {
                <p class="mt-1 text-sm text-slate-600 dark:text-slate-300 whitespace-pre-line">{{ message() }}</p>
              }
            </div>
          </div>

          <!-- Optional projected content (extra warnings, lists, etc.) -->
          <ng-content></ng-content>

          <!-- Buttons -->
          <div class="flex justify-end gap-2 pt-2">
            @if (variant() !== 'success') {
              <button type="button"
                      class="touch-target px-4 py-2 rounded-lg bg-slate-100 dark:bg-slate-700 hover:bg-slate-200 dark:hover:bg-slate-600 text-slate-900 dark:text-white font-medium"
                      [disabled]="busy()"
                      (click)="onCancel()">
                {{ cancelLabel() }}
              </button>
            }
            <button type="button"
                    [class]="confirmBtnClass()"
                    class="touch-target px-4 py-2 rounded-lg text-white font-semibold disabled:opacity-50 disabled:pointer-events-none"
                    [disabled]="busy()"
                    (click)="onConfirm()">
              {{ busy() ? (busyLabel() || confirmLabel()) : confirmLabel() }}
            </button>
          </div>
        </div>
      </div>
    }
  `,
})
export class ModalComponent {
  open = input<boolean>(false);
  variant = input<'confirm' | 'danger' | 'success'>('confirm');
  title = input<string>('');
  message = input<string>('');
  confirmLabel = input<string>('OK');
  cancelLabel = input<string>('Zrušiť');
  /** Optional "busy" label shown while confirm() handler is running (e.g. "Odstraňujem..."). */
  busyLabel = input<string>('');
  /** Disable buttons + show busy label while a parent action is in flight. */
  busy = input<boolean>(false);
  /** Clicking backdrop closes the modal (false for required interactions). */
  dismissable = input<boolean>(true);

  confirm = output<void>();
  cancel = output<void>();

  iconWrapClass = computed(() => {
    switch (this.variant()) {
      case 'success': return 'bg-green-100 dark:bg-green-900/40 text-green-600 dark:text-green-300';
      case 'danger':  return 'bg-red-100 dark:bg-red-900/40 text-red-600 dark:text-red-300';
      default:        return 'bg-amber-100 dark:bg-amber-900/40 text-amber-600 dark:text-amber-300';
    }
  });

  confirmBtnClass = computed(() => {
    switch (this.variant()) {
      case 'success': return 'bg-green-600 hover:bg-green-700';
      case 'danger':  return 'bg-red-600 hover:bg-red-700';
      default:        return 'bg-amber-500 hover:bg-amber-600';
    }
  });

  onConfirm() { this.confirm.emit(); }
  onCancel()  { this.cancel.emit(); }

  onBackdrop(_: MouseEvent) {
    if (this.dismissable() && !this.busy()) this.onCancel();
  }
}
