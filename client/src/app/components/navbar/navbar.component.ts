import { Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive } from '@angular/router';
import { filter } from 'rxjs';
import { AuthService } from '../../services/auth.service';
import { ThemeService } from '../../services/theme.service';
import { FeatureFlagService } from '../../services/feature-flag.service';
import { Division, DivisionService, DIVISION_LABELS } from '../../services/division.service';

type NavSide = 'operations' | 'finance';

/** A primary navigation entry: route, label, and a single-path stroke icon. */
type NavLink = { path: string; label: string; icon: string; exact?: boolean };

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
  division = inject(DivisionService);
  private router = inject(Router);

  /** Division "burger" dropdown (replaces the Faktúry nav entry — Fáza D). */
  divMenuOpen = signal(false);
  readonly divisions: { key: Division; label: string; hint: string }[] = [
    { key: 'profistav', label: DIVISION_LABELS.profistav, hint: 'stavby' },
    { key: 'stroje',    label: DIVISION_LABELS.stroje,    hint: 'stroje' }
  ];

  pickDivision(d: Division) {
    this.division.set(d);
    this.divMenuOpen.set(false);
    this.menuOpen.set(false);
    this.router.navigateByUrl('/admin/invoices');
  }

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

  /** Shared landing link, shown before the side switcher. */
  readonly homeLink: NavLink = { path: '/admin/dashboard', label: 'Prehľad', icon: 'M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0a1 1 0 001-1v-4a1 1 0 011-1h2a1 1 0 011 1v4a1 1 0 001 1m-6 0h6' };

  /** Operations-side links (feature-flag filtered). Getters re-evaluate flags
   *  every change-detection pass, matching the previous inline @if behaviour. */
  get opsLinks(): NavLink[] {
    const su = this.auth.isSuperAdmin();
    return [
      { path: '/admin/employees', label: 'Zamestnanci', icon: 'M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0zm6 3a2 2 0 11-4 0 2 2 0 014 0zM7 10a2 2 0 11-4 0 2 2 0 014 0z' },
      { path: '/admin/locations', label: 'Pracoviská', icon: 'M17.657 16.657L13.414 20.9a1.998 1.998 0 01-2.827 0l-4.244-4.243a8 8 0 1111.314 0z' },
      { path: '/admin/cars', label: 'Autá', icon: 'M13 16V6a1 1 0 00-1-1H4a1 1 0 00-1 1v10a1 1 0 001 1h1m8-1a1 1 0 01-1 1H9m4-1V8a1 1 0 011-1h2.586a1 1 0 01.707.293l3.414 3.414a1 1 0 01.293.707V16a1 1 0 01-1 1h-1m-6-1a1 1 0 001 1h1M5 17a2 2 0 104 0m-4 0a2 2 0 114 0m6 0a2 2 0 104 0m-4 0a2 2 0 014 0' },
      { path: '/admin/stroje', label: 'Mašiny/Tatry', icon: 'M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z M15 12a3 3 0 11-6 0 3 3 0 016 0z' },
      { path: '/admin/palivove-karty', label: 'Karty', icon: 'M3 10h18M7 15h1m4 0h1m-7 4h12a3 3 0 003-3V8a3 3 0 00-3-3H6a3 3 0 00-3 3v8a3 3 0 003 3z' },
      ...(this.flags.planner() || su ? [{ path: '/admin/planner', label: 'Plánovač', icon: 'M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z' }] : []),
      ...(this.flags.commanderIntegration() || su ? [{ path: '/admin/commander', label: 'Commander', icon: 'M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z' }] : []),
      ...(this.flags.notifications() || su ? [{ path: '/admin/notifikacie', label: 'Notifikácie', icon: 'M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9' }] : []),
      { path: '/admin/time-entries', label: 'Záznamy', icon: 'M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z' },
    ];
  }

  /** Finance-side links (feature-flag filtered). */
  get finLinks(): NavLink[] {
    const su = this.auth.isSuperAdmin();
    return [
      { path: '/admin/finance', label: 'Súhrn', exact: true, icon: 'M4 5a1 1 0 011-1h4a1 1 0 011 1v4a1 1 0 01-1 1H5a1 1 0 01-1-1V5zM14 5a1 1 0 011-1h4a1 1 0 011 1v4a1 1 0 01-1 1h-4a1 1 0 01-1-1V5zM4 15a1 1 0 011-1h4a1 1 0 011 1v4a1 1 0 01-1 1H5a1 1 0 01-1-1v-4zM14 15a1 1 0 011-1h4a1 1 0 011 1v4a1 1 0 01-1 1h-4a1 1 0 01-1-1v-4z' },
      { path: '/admin/materials', label: 'Materiál', icon: 'M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4' },
      ...(this.flags.invoiceScanning() || su ? [{ path: '/admin/invoices', label: 'Faktúry', icon: 'M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z' }] : []),
      ...(this.flags.payrollAndPnL() || su ? [{ path: '/admin/mzdy', label: 'Mzdy', icon: 'M17 9V7a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2m2 4h10a2 2 0 002-2v-6a2 2 0 00-2-2H9a2 2 0 00-2 2v6a2 2 0 002 2zm7-5a2 2 0 11-4 0 2 2 0 014 0z' }] : []),
      ...(this.flags.payrollAndPnL() || su ? [{ path: '/admin/odvody', label: 'Odvody', icon: 'M9 14l6-6m-5.5.5h.01m4.99 5h.01M19 21V5a2 2 0 00-2-2H7a2 2 0 00-2 2v16l3.5-2 3.5 2 3.5-2 3.5 2z' }] : []),
    ];
  }

  /** The link row for the currently active side. */
  get activeLinks(): NavLink[] {
    return this.side() === 'finance' ? this.finLinks : this.opsLinks;
  }

  /** 1–2 letter monogram for the account avatar. */
  get initials(): string {
    const n = (this.auth.displayName() || '').trim();
    if (!n) return '?';
    const parts = n.split(/\s+/).filter(Boolean);
    return (parts.length > 1 ? parts[0][0] + parts[1][0] : n.slice(0, 2)).toUpperCase();
  }

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
