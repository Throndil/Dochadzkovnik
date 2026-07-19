import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';

/**
 * Touch-friendly number field with −/+ steppers (Vzduch redesign). Replaces
 * the tiny native spinners (hidden globally in styles.css) that stepped by
 * 0.01 — the buttons here step by ±1; decimals stay typeable in the input.
 */
@Component({
  selector: 'app-stepper',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="inline-flex items-stretch rounded-lg border border-slate-300 dark:border-slate-600 bg-white dark:bg-slate-700 overflow-hidden"
         [class.opacity-50]="disabled()">
      <button type="button" (click)="bump(-1)" [disabled]="disabled()" tabindex="-1" aria-label="Menej"
              class="touch-target w-9 flex items-center justify-center text-lg text-slate-500 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-600 active:bg-slate-200 dark:active:bg-slate-500 transition-colors select-none">−</button>
      <input type="number" inputmode="decimal" step="1" [min]="min()"
             [value]="value()" (input)="onInput($event)" [disabled]="disabled()"
             (keyup.enter)="enter.emit()" (keyup.escape)="escape.emit()"
             [placeholder]="placeholder()"
             class="w-16 py-1.5 text-center text-sm tabular-nums bg-transparent text-slate-900 dark:text-white outline-none border-x border-slate-200 dark:border-slate-600" />
      <button type="button" (click)="bump(1)" [disabled]="disabled()" tabindex="-1" aria-label="Viac"
              class="touch-target w-9 flex items-center justify-center text-lg text-slate-500 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-600 active:bg-slate-200 dark:active:bg-slate-500 transition-colors select-none">+</button>
      @if (suffix()) {
        <span class="flex items-center pr-2.5 pl-0.5 text-xs text-slate-400 dark:text-slate-500 whitespace-nowrap">{{ suffix() }}</span>
      }
    </div>
  `
})
export class StepperComponent {
  /** Two-way bound value; null = empty field. */
  value = model<number | null>(null);
  min = input(0);
  suffix = input('');
  placeholder = input('');
  disabled = input(false);
  enter = output<void>();
  escape = output<void>();

  bump(dir: 1 | -1) {
    const next = Math.max(this.min(), Math.round(((this.value() ?? 0) + dir) * 100) / 100);
    this.value.set(next);
  }

  onInput(event: Event) {
    const raw = (event.target as HTMLInputElement).value;
    this.value.set(raw === '' ? null : Number(raw));
  }
}
