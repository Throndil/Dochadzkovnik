import { Component, signal, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { NavbarComponent } from '../../components/navbar/navbar.component';
import { AlertComponent } from '../../components/alert/alert.component';
import { AuthService } from '../../services/auth.service';
import { FeatureFlagService } from '../../services/feature-flag.service';

@Component({
  selector: 'app-account',
  imports: [NavbarComponent, FormsModule, AlertComponent],
  templateUrl: './account.page.html'
})
export class AccountPage implements OnInit {
  // Recovery email
  recoveryEmail = '';
  adminUsername = signal('');
  emailSaving = signal(false);
  emailSaved = signal(false);
  emailError = signal('');

  // Change password
  currentPassword = '';
  newPassword = '';
  confirmPassword = '';
  pwdSaving = signal(false);
  pwdSaved = signal(false);
  pwdError = signal('');

  // Funkcie (superadmin only) — toggles for hidden features
  flags = inject(FeatureFlagService);
  flagSaving = signal(false);
  flagError = signal('');

  get pwdValid() {
    return this.currentPassword && this.newPassword.length >= 6 && this.newPassword === this.confirmPassword;
  }

  constructor(public auth: AuthService) {}

  /**
   * Toggle a flag and surface any error in the card. Re-fetched after the PUT
   * succeeds so the local signal stays in sync with the server's authoritative
   * state.
   */
  async onToggleFlag(key: string, enabled: boolean) {
    this.flagError.set('');
    this.flagSaving.set(true);
    try {
      await this.flags.setEnabled(key, enabled);
    } catch {
      this.flagError.set('Zmenu sa nepodarilo uložiť. Skús znovu.');
    } finally {
      this.flagSaving.set(false);
    }
  }

  ngOnInit() {
    this.auth.getMe().subscribe({ next: (me) => {
      this.recoveryEmail = me.email ?? '';
      this.adminUsername.set(me.userName ?? '');
    }});
  }

  onSaveEmail() {
    this.emailError.set('');
    this.emailSaved.set(false);
    this.emailSaving.set(true);
    this.auth.updateRecoveryEmail(this.recoveryEmail).subscribe({
      next: () => { this.emailSaved.set(true); this.emailSaving.set(false); },
      error: () => { this.emailError.set('Uloženie sa nepodarilo.'); this.emailSaving.set(false); }
    });
  }

  onChangePassword() {
    if (!this.pwdValid) return;
    this.pwdError.set('');
    this.pwdSaved.set(false);
    this.pwdSaving.set(true);
    this.auth.changePassword(this.currentPassword, this.newPassword).subscribe({
      next: () => {
        this.pwdSaved.set(true);
        this.pwdSaving.set(false);
        this.currentPassword = '';
        this.newPassword = '';
        this.confirmPassword = '';
      },
      error: (err) => { this.pwdError.set(err.error ?? 'Zmena hesla sa nepodarila.'); this.pwdSaving.set(false); }
    });
  }
}
