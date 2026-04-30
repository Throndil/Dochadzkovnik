import { Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../../services/auth.service';
import { ThemeService } from '../../services/theme.service';
import { FeatureFlagService } from '../../services/feature-flag.service';

@Component({
  selector: 'app-navbar',
  imports: [RouterLink, RouterLinkActive],
  templateUrl: './navbar.component.html'
})
export class NavbarComponent {
  menuOpen = signal(false);
  theme = inject(ThemeService);
  flags = inject(FeatureFlagService);

  constructor(public auth: AuthService) {}

  onLogout() {
    this.auth.logout();
    window.location.href = '/login';
  }
}
