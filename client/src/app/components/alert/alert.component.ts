import { Component, computed, input } from '@angular/core';

/**
 * Unified inline alert/banner — THE one way to show errors, warnings,
 * successes and info boxes across the admin UI. Replaces the ~8 divergent
 * Tailwind recipes found in the UX audit (some unreadable in light mode).
 *
 * Two usage modes:
 *   1. Signal-bound message (auto-hides when empty/null):
 *        <app-alert variant="error" [message]="error()" />
 *   2. Projected rich body (wrap in your own @if, set alwaysRender):
 *        @if (quotaSpent()) {
 *          <app-alert variant="warning" [alwaysRender]="true">
 *            <strong>Denný limit</strong> je vyčerpaný…
 *          </app-alert>
 *        }
 *
 * `big` = outage-grade emphasis (thicker border, larger padding, bold).
 */
@Component({
  selector: 'app-alert',
  standalone: true,
  template: `
    @if (message() || alwaysRender()) {
      <div class="rounded-lg text-sm flex items-start gap-2.5"
           [class]="wrapperClass()"
           role="alert">
        <span class="shrink-0 leading-none pt-0.5" aria-hidden="true">{{ icon() }}</span>
        <div class="min-w-0" [class.font-semibold]="big()">{{ message() }}<ng-content /></div>
      </div>
    }
  `
})
export class AlertComponent {
  variant = input<'error' | 'warning' | 'success' | 'info'>('error');
  /** Main text; when empty and alwaysRender is false, nothing renders. */
  message = input<string | null | undefined>('');
  /** Set when using projected content instead of [message]. */
  alwaysRender = input(false);
  /** Outage-grade emphasis. */
  big = input(false);

  protected icon = computed(() => ({
    error: '⚠️',
    warning: '⚠️',
    success: '✓',
    info: 'ℹ️'
  })[this.variant()]);

  protected wrapperClass = computed(() => {
    const pad = this.big() ? 'p-4 border-2 rounded-xl' : 'p-3 border';
    const colors = {
      error:   'bg-red-100 dark:bg-red-900/40 border-red-300 dark:border-red-700 text-red-700 dark:text-red-300',
      warning: 'bg-amber-50 dark:bg-amber-900/30 border-amber-300 dark:border-amber-700 text-amber-800 dark:text-amber-200',
      success: 'bg-green-100 dark:bg-green-900/30 border-green-300 dark:border-green-700 text-green-800 dark:text-green-300',
      info:    'bg-blue-50 dark:bg-blue-900/30 border-blue-300 dark:border-blue-700 text-blue-800 dark:text-blue-200'
    }[this.variant()];
    return `${pad} ${colors}`;
  });
}
