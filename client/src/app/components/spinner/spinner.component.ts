import { Component, input } from '@angular/core';

/**
 * Small standalone loading spinner. Used wherever a page is fetching its
 * initial data and we don't want the empty-state copy ("Žiadne X pridané")
 * to flash before the request resolves.
 *
 * Usage:
 *   <app-spinner />                          ← centred, medium
 *   <app-spinner size="sm" />                ← inline, small
 *   <app-spinner label="Načítavam..." />     ← with caption
 *   <app-spinner inline="true" />            ← no vertical padding wrapper
 */
@Component({
  selector: 'app-spinner',
  standalone: true,
  template: `
    <div class="flex flex-col items-center justify-center gap-3"
         [class.py-10]="!inline()"
         [class.py-0]="inline()">
      <svg class="animate-spin text-amber-500"
           [class.w-5]="size() === 'sm'"
           [class.h-5]="size() === 'sm'"
           [class.w-10]="size() === 'md'"
           [class.h-10]="size() === 'md'"
           [class.w-14]="size() === 'lg'"
           [class.h-14]="size() === 'lg'"
           viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" role="status" aria-label="Načítavam">
        <circle cx="12" cy="12" r="10" stroke="currentColor" stroke-width="3" stroke-opacity="0.2"/>
        <path d="M22 12a10 10 0 0 1-10 10" stroke="currentColor" stroke-width="3" stroke-linecap="round"/>
      </svg>
      @if (label()) {
        <p class="text-sm text-slate-600 dark:text-slate-300">{{ label() }}</p>
      }
    </div>
  `,
})
export class SpinnerComponent {
  size = input<'sm' | 'md' | 'lg'>('md');
  label = input<string>('');
  inline = input<boolean>(false);
}
