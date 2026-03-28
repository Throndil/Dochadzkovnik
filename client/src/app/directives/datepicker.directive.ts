import { Directive, ElementRef, Input, AfterViewInit, OnDestroy } from '@angular/core';
import flatpickr from 'flatpickr';
import type { Instance } from 'flatpickr/dist/types/instance';

@Directive({
  selector: 'input[appDate]',
  standalone: true
})
export class DatepickerDirective implements AfterViewInit, OnDestroy {
  @Input() enableTime = false;

  private _maxDate = '';
  private _minDate = '';
  private fp?: Instance;

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
    this.fp = flatpickr(el, {
      dateFormat: this.enableTime ? 'Y-m-dTH:i' : 'Y-m-d',
      altInput: true,
      altFormat: this.enableTime ? 'd.m.Y H:i' : 'd.m.Y',
      enableTime: this.enableTime,
      time_24hr: true,
      disableMobile: true,
      allowInput: false,
      defaultDate: el.value || undefined,
      maxDate: this._maxDate || undefined,
      minDate: this._minDate || undefined,
      locale: { firstDayOfWeek: 1 } as any,
      onChange: (_d, dateStr) => {
        const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
        setter.call(el, dateStr);
        el.dispatchEvent(new Event('input', { bubbles: true }));
      }
    }) as Instance;

    // ngModel may write the value after AfterViewInit; check again after a tick
    setTimeout(() => {
      if (el.value && !this.fp?.selectedDates.length) {
        this.fp?.setDate(el.value, false);
      }
    });
  }

  ngOnDestroy() {
    this.fp?.destroy();
  }
}
