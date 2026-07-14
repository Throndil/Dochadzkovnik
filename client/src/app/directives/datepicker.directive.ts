import { Directive, ElementRef, Input, AfterViewInit, OnDestroy, forwardRef } from '@angular/core';
import { ControlValueAccessor, NG_VALUE_ACCESSOR } from '@angular/forms';
import flatpickr from 'flatpickr';
import type { Instance } from 'flatpickr/dist/types/instance';

/** Slovak flatpickr locale, inlined so we don't deep-import
 *  `flatpickr/dist/l10n/sk.js`. That CommonJS deep import (flatpickr ships no
 *  package `exports` map) destabilises Angular's dev-server dependency
 *  optimizer, so the month/weekday names are provided here instead. */
const SLOVAK_L10N: any = {
  weekdays: {
    shorthand: ['ne', 'po', 'ut', 'st', 'št', 'pi', 'so'],
    longhand: ['nedeľa', 'pondelok', 'utorok', 'streda', 'štvrtok', 'piatok', 'sobota'],
  },
  months: {
    shorthand: ['jan', 'feb', 'mar', 'apr', 'máj', 'jún', 'júl', 'aug', 'sep', 'okt', 'nov', 'dec'],
    longhand: ['január', 'február', 'marec', 'apríl', 'máj', 'jún', 'júl', 'august', 'september', 'október', 'november', 'december'],
  },
  firstDayOfWeek: 1,
  rangeSeparator: ' – ',
  weekAbbreviation: 'Týž.',
  ordinal: () => '.',
};

/**
 * Wraps flatpickr on an <input appDate> element.
 *
 * This directive also implements ControlValueAccessor so that PROGRAMMATIC
 * `ngModel` / `formControl` updates (e.g. a "Týždeň" quick-range button that
 * reassigns `this.from = 'YYYY-MM-DD'` on the component) are pushed into
 * flatpickr via `fp.setDate(...)`. Without that, flatpickr's visible alt-input
 * continues to show the previous value because flatpickr only reads
 * `input.value` at initialisation.
 */
@Directive({
  selector: 'input[appDate]',
  standalone: true,
  providers: [{
    provide: NG_VALUE_ACCESSOR,
    useExisting: forwardRef(() => DatepickerDirective),
    multi: true
  }]
})
export class DatepickerDirective implements AfterViewInit, OnDestroy, ControlValueAccessor {
  @Input() enableTime = false;

  private _maxDate = '';
  private _minDate = '';
  private fp?: Instance;

  // ControlValueAccessor plumbing
  private pendingValue: string | null = null;
  private onChangeCb: (value: string) => void = () => {};
  private onTouchedCb: () => void = () => {};

  @Input() set fpMaxDate(v: string) {
    this._maxDate = v;
    this.fp?.set('maxDate', v || null);
  }

  @Input() set fpMinDate(v: string) {
    this._minDate = v;
    this.fp?.set('minDate', v || null);
  }

  constructor(private el: ElementRef<HTMLInputElement>) {}

  ngAfterViewInit() {
    const el = this.el.nativeElement;
    const initial = this.pendingValue ?? el.value ?? '';
    const onChange = (_d: Date[], dateStr: string) => {
      // Mirror the value back to the raw input and notify BOTH ngModel (CVA)
      // and plain (change)/(input) template bindings.
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
      setter.call(el, dateStr);
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      this.onChangeCb(dateStr);
      this.onTouchedCb();
    };
    const base: any = {
      altInput: true,
      // Copy the host input's classes onto flatpickr's visible alt-input so a bare
      // `appDate` keeps whatever styling the input already had. Inputs wrapped in
      // `.date-field` have no class here and are styled by that wrapper instead.
      altInputClass: el.className,
      disableMobile: true,
      allowInput: false,
      defaultDate: initial || undefined,
      maxDate: this._maxDate || undefined,
      minDate: this._minDate || undefined,
      locale: SLOVAK_L10N,
      onChange
    };
    this.fp = flatpickr(el, {
      ...base,
      dateFormat: this.enableTime ? 'Y-m-dTH:i' : 'Y-m-d',
      altFormat: this.enableTime ? 'd.m.Y H:i' : 'd.m.Y',
      enableTime: this.enableTime,
      time_24hr: true
    }) as Instance;

    // If the host applied a value via writeValue BEFORE flatpickr was ready,
    // apply it now. Also handles the case where ngModel wrote to the input
    // just after AfterViewInit (we still want flatpickr's alt-input in sync).
    if (this.pendingValue !== null) {
      this.fp.setDate(this.pendingValue, false);
      this.pendingValue = null;
    } else if (el.value && !this.fp.selectedDates.length) {
      this.fp.setDate(el.value, false);
    }
  }

  // ── ControlValueAccessor ──────────────────────────────────────────
  writeValue(value: string | null | undefined): void {
    const v = value ?? '';
    if (this.fp) {
      // `false` = don't fire flatpickr's onChange → avoids ngModel feedback loop
      // Passing '' clears the selection.
      this.fp.setDate(v, false);
    } else {
      this.pendingValue = v;
    }
  }

  registerOnChange(fn: (value: string) => void): void {
    this.onChangeCb = fn;
  }

  registerOnTouched(fn: () => void): void {
    this.onTouchedCb = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    const el = this.el.nativeElement;
    el.disabled = isDisabled;
    // flatpickr has no separate disabled prop; disabling the underlying
    // input is enough — the picker won't open.
  }

  ngOnDestroy() {
    this.fp?.destroy();
  }
}
