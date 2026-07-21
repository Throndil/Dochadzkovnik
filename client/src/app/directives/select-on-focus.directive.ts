import { Directive, HostListener } from '@angular/core';

/**
 * Selects the input's whole content on focus, so typing immediately replaces
 * the prefilled value — nobody hand-deletes "2,00" before entering a price.
 * Applied to the editable number/text cells on the invoice review screen.
 */
@Directive({ selector: 'input[appSelectOnFocus]', standalone: true })
export class SelectOnFocusDirective {
  @HostListener('focus', ['$event'])
  onFocus(ev: FocusEvent) {
    (ev.target as HTMLInputElement | null)?.select();
  }
}
