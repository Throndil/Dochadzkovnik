import { Directive, ElementRef, Input, AfterViewInit, OnDestroy } from '@angular/core';
import flatpickr from 'flatpickr';
import type { Instance } from 'flatpickr/dist/types/instance';

@Directive({
  selector: 'input[appTime]',
  standalone: true
})
export class TimepickerDirective implements AfterViewInit, OnDestroy {
  private _minTime = '';
  private fp?: Instance;

  @Input() set fpMinTime(v: string) {
    this._minTime = v;
    this.fp?.set('minTime', v || null);
  }

  constructor(private el: ElementRef<HTMLInputElement>) {}

  ngAfterViewInit() {
    const el = this.el.nativeElement;
    this.fp = flatpickr(el, {
      noCalendar: true,
      enableTime: true,
      time_24hr: true,
      dateFormat: 'H:i',
      defaultDate: el.value || undefined,
      minTime: this._minTime || undefined,
      disableMobile: true,
      onChange: (_d, timeStr) => {
        const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
        setter.call(el, timeStr);
        el.dispatchEvent(new Event('input', { bubbles: true }));
      }
    }) as Instance;

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
