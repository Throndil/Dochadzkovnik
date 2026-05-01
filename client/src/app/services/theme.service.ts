import { Injectable, signal } from '@angular/core';

// Theme colour for the browser/OS chrome (iOS Safari status bar, Android
// Chrome toolbar, PWA splash). Must match the page background users see.
//   Light: matches the admin page wrapper bg-slate-50.
//   Dark : matches the admin page wrapper bg-slate-900. Slate-900 is what
//          bleeds into the status bar / overscroll area on iOS, so the
//          chrome should match it to look continuous with the page.
const THEME_COLOR_LIGHT = '#f8fafc';
const THEME_COLOR_DARK  = '#0f172a';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly STORAGE_KEY = 'theme';

  isDark = signal<boolean>(this.loadPreference());

  private loadPreference(): boolean {
    const saved = localStorage.getItem(this.STORAGE_KEY);
    if (saved !== null) return saved === 'dark';
    return window.matchMedia('(prefers-color-scheme: dark)').matches;
  }

  applyTheme(): void {
    const dark = this.isDark();
    if (dark) {
      document.documentElement.classList.add('dark');
    } else {
      document.documentElement.classList.remove('dark');
    }
    this.applyThemeColorMeta(dark);
  }

  toggleTheme(): void {
    this.isDark.update(v => !v);
    localStorage.setItem(this.STORAGE_KEY, this.isDark() ? 'dark' : 'light');
    this.applyTheme();
  }

  // Keep the iOS Safari status bar / Android Chrome toolbar in sync with the
  // app's dark-mode toggle. The static media-scoped <meta name="theme-color">
  // tags in index.html only follow the OS preference; when OS=light and
  // app=dark, the status bar would render white. We maintain a third unscoped
  // theme-color tag here that follows the app state and overrides the others.
  private applyThemeColorMeta(dark: boolean): void {
    const colour = dark ? THEME_COLOR_DARK : THEME_COLOR_LIGHT;
    let tag = document.querySelector<HTMLMetaElement>(
      'meta[name="theme-color"]:not([media])'
    );
    if (!tag) {
      tag = document.createElement('meta');
      tag.setAttribute('name', 'theme-color');
      document.head.appendChild(tag);
    }
    tag.setAttribute('content', colour);
  }
}
