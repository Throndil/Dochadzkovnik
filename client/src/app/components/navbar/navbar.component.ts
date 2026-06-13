import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from '../../services/auth.service';
import { ThemeService } from '../../services/theme.service';
import { FeatureFlagService } from '../../services/feature-flag.service';

type NavSide = 'operations' | 'finance';

/** URL prefixes that unambiguously belong to each side. Pages not listed here
 *  (the shared dashboard, account) are "neutral" and fall back to the
 *  remembered side, so reopening the app lands you where you left off. */
const FINANCE_PREFIXES = ['/admin/finance', '/admin/materials', '/admin/invoices', '/admin/mzdy'];
const OPERATIONS_PREFIXES = [
  '/admin/employees', '/admin/locations', '/admin/cars',
  '/admin/commander', '/admin/notifikacie', '/admin/time-entries'
];

const SIDE_STORAGE_KEY = 'navSide';

@Component({
  selector: 'app-navbar',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './navbar.component.html'
})
export class NavbarComponent {
  menuOpen = signal(false);
  theme = inject(ThemeService);
  flags = inject(FeatureFlagService);
  private router = inject(Router);

  /** Current URL, kept in sync with navigation so `side` recomputes on route
   *  changes (deep links and refreshes included). */
  private currentUrl = signal(this.router.url);

  /** Last side the user was on, persisted across sessions. Drives the nav on
   *  neutral pages where the URL doesn't pick a side. */
  private preferredSide = signal<NavSide>(
    localStorage.getItem(SIDE_STORAGE_KEY) === 'finance' ? 'finance' : 'operations'
  );

  /** Active side: derived from the URL when it's a sided page, else the
   *  remembered preference. */
  side = computed<NavSide>(() => this.sideFromUrl(this.currentUrl()) ?? this.preferredSide());

  constructor(public auth: AuthService) {
    // Honour the side of the URL we booted on.
    this.rememberIfSided(this.router.url);
    this.router.events
      .pipe(filter(e => e instanceof NavigationEnd))
      .subscribe(e => {
        const url = (e as NavigationEnd).urlAfterRedirects;
        this.currentUrl.set(url);
        this.rememberIfSided(url);
      });
  }

  /** Returns the side a URL definitively belongs to, or null if it's neutral. */
  private sideFromUrl(url: string): NavSide | null {
    if (FINANCE_PREFIXES.some(p => url.startsWith(p))) return 'finance';
    if (OPERATIONS_PREFIXES.some(p => url.startsWith(p))) return 'operations';
    return null;
  }

  private rememberIfSided(url: string) {
    const definitive = this.sideFromUrl(url);
    if (definitive) this.setPreferred(definitive);
  }

  private setPreferred(side: NavSide) {
    this.preferredSide.set(side);
    localStorage.setItem(SIDE_STORAGE_KEY, side);
  }

  /** Classes for a segmented-switcher button. Kept in TS so the active/inactive
   *  states stay in sync without a CommonModule dependency. */
  segClass(active: boolean): string {
    return (
      'px-3 py-1.5 rounded-md text-sm font-medium transition-colors ' +
      (active
        ? 'bg-white dark:bg-slate-900 text-amber-600 dark:text-amber-400 shadow-sm'
        : 'text-slate-600 dark:text-slate-300 hover:text-slate-900 dark:hover:text-white')
    );
  }

  /** Switch sides by navigating to that side's landing page. */
  switchSide(side: NavSide) {
    this.menuOpen.set(false);
    this.setPreferred(side);
    this.router.navigateByUrl(side === 'finance' ? '/admin/finance' : '/admin/dashboard');
  }

  onLogout() {
    this.auth.logout();
    window.location.href = '/login';
  }
}
