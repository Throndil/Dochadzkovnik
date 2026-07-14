import { Component, forwardRef, signal, computed, Input } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';

const SK_MONTHS_LONG = ['január', 'február', 'marec', 'apríl', 'máj', 'jún', 'júl', 'august', 'september', 'október', 'november', 'december'];
const SK_MONTHS_SHORT = ['jan', 'feb', 'mar', 'apr', 'máj', 'jún', 'júl', 'aug', 'sep', 'okt', 'nov', 'dec'];

/**
 * Slovak month picker. Self-contained (no external date library) so it doesn't
 * depend on flatpickr's monthSelect plugin — that plugin is a deep CommonJS
 * import into flatpickr's dist, which breaks Angular's dev-server dependency
 * optimizer. Value is a 'YYYY-MM' string (empty = nothing selected), matching
 * what the native <input type="month"> used to emit, and it works as a
 * ControlValueAccessor so [(ngModel)] drops straight in.
 */
@Component({
  selector: 'app-month-picker',
  standalone: true,
  providers: [{ provide: NG_VALUE_ACCESSOR, useExisting: forwardRef(() => MonthPickerComponent), multi: true }],
  template: `
  <div class="relative inline-block">
    <button type="button" (click)="toggle()" [disabled]="disabled()"
            class="inline-flex items-center gap-2 min-w-[9rem] px-3 py-2 rounded-lg border border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-800 text-sm text-slate-900 dark:text-white hover:border-amber-400 focus:outline-none focus:ring-2 focus:ring-amber-500 transition-colors disabled:opacity-50 disabled:pointer-events-none">
      <svg class="w-4 h-4 text-amber-500 shrink-0" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
        <rect x="3" y="4" width="18" height="18" rx="2" ry="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/>
      </svg>
      <span [class.text-slate-400]="!value()" [class.dark:text-slate-500]="!value()">{{ label() }}</span>
    </button>

    @if (open()) {
      <!-- Click-catcher closes the panel when clicking elsewhere. -->
      <div class="fixed inset-0 z-40" (click)="close()"></div>
      <div class="absolute left-0 z-50 mt-1 w-64 rounded-xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-800 shadow-xl p-3">
        <div class="flex items-center justify-between mb-2">
          <button type="button" (click)="viewYear.set(viewYear() - 1)" aria-label="Predchádzajúci rok"
                  class="w-8 h-8 flex items-center justify-center rounded-lg hover:bg-slate-100 dark:hover:bg-slate-700 text-slate-600 dark:text-slate-300 text-lg leading-none">‹</button>
          <span class="font-semibold text-slate-900 dark:text-white tabular-nums">{{ viewYear() }}</span>
          <button type="button" (click)="viewYear.set(viewYear() + 1)" aria-label="Nasledujúci rok"
                  class="w-8 h-8 flex items-center justify-center rounded-lg hover:bg-slate-100 dark:hover:bg-slate-700 text-slate-600 dark:text-slate-300 text-lg leading-none">›</button>
        </div>
        <div class="grid grid-cols-3 gap-1">
          @for (m of months; track m.i) {
            <button type="button" (click)="pick(m.i)" [class]="cellClass(m.i)"
                    class="px-2 py-2 rounded-lg text-sm transition-colors">{{ m.short }}</button>
          }
        </div>
        @if (clearable) {
          <button type="button" (click)="clear()"
                  class="mt-2 w-full px-3 py-1.5 rounded-lg text-sm text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-700 transition-colors">
            {{ clearLabel }}
          </button>
        }
      </div>
    }
  </div>
  `
})
export class MonthPickerComponent implements ControlValueAccessor {
  /** Text shown when no month is selected. */
  @Input() placeholder = 'Vyberte mesiac';
  /** When true, shows a button that clears the selection (value → ''). */
  @Input() clearable = false;
  @Input() clearLabel = 'Celá história';

  /** Selected value as 'YYYY-MM', or '' when nothing is picked. */
  value = signal<string>('');
  open = signal(false);
  disabled = signal(false);
  viewYear = signal(new Date().getFullYear());

  readonly months = SK_MONTHS_SHORT.map((short, i) => ({ i, short }));

  label = computed(() => {
    const v = this.value();
    if (!v) return this.placeholder;
    const [y, m] = v.split('-');
    return `${SK_MONTHS_LONG[+m - 1]} ${y}`;
  });

  private onChange: (v: string) => void = () => {};
  private onTouched: () => void = () => {};

  toggle() {
    if (this.open()) { this.close(); return; }
    const v = this.value();
    if (v) this.viewYear.set(+v.split('-')[0]);
    this.open.set(true);
  }

  close() {
    this.open.set(false);
    this.onTouched();
  }

  pick(monthIndex: number) {
    const v = `${this.viewYear()}-${String(monthIndex + 1).padStart(2, '0')}`;
    this.value.set(v);
    this.onChange(v);
    this.close();
  }

  clear() {
    this.value.set('');
    this.onChange('');
    this.close();
  }

  /** Highlights the selected month within the year currently in view. */
  cellClass(i: number): string {
    const v = this.value();
    const selected = !!v && +v.split('-')[0] === this.viewYear() && +v.split('-')[1] === i + 1;
    return selected
      ? 'bg-amber-500 text-white'
      : 'text-slate-700 dark:text-slate-200 hover:bg-amber-100 dark:hover:bg-slate-700';
  }

  // ── ControlValueAccessor ──────────────────────────────────────────
  writeValue(v: string | null): void { this.value.set(v ?? ''); }
  registerOnChange(fn: (v: string) => void): void { this.onChange = fn; }
  registerOnTouched(fn: () => void): void { this.onTouched = fn; }
  setDisabledState(isDisabled: boolean): void { this.disabled.set(isDisabled); }
}
