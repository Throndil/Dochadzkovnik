import { Component, input } from '@angular/core';

/**
 * Unified empty-state card — the audit found three competing treatments
 * (styled card / bare text / italic card). This is the canonical one.
 *
 *   <app-empty-state message="Žiadne faktúry. Začnite nahratím PDF." />
 *   <app-empty-state icon="📄" message="Žiadne dáta.">
 *     <button …>+ Pridať</button>          ← optional CTA slot
 *   </app-empty-state>
 */
@Component({
  selector: 'app-empty-state',
  standalone: true,
  template: `
    <div class="bg-white dark:bg-slate-800 rounded-xl border border-slate-200 dark:border-slate-700 p-8 text-center text-slate-500 dark:text-slate-400">
      @if (icon()) {
        <div class="text-3xl mb-2" aria-hidden="true">{{ icon() }}</div>
      }
      <div class="text-sm">{{ message() }}</div>
      <div class="mt-3 empty:hidden"><ng-content /></div>
    </div>
  `
})
export class EmptyStateComponent {
  message = input.required<string>();
  /** Optional emoji/icon shown above the message. */
  icon = input<string>('');
}
