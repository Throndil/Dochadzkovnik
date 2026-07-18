import { Component, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../services/auth.service';
import { ThemeService } from '../../services/theme.service';

@Component({
  selector: 'app-login',
  imports: [FormsModule, RouterLink],
  templateUrl: './login.page.html'
})
export class LoginPage {
  username = '';
  password = '';
  pin = '';
  /** True after a correct password on a PIN-protected account — the form
   *  switches to the PIN step (second gate against a guessed password). */
  pinStep = signal(false);
  error = signal('');
  loading = signal(false);

  constructor(private auth: AuthService, private router: Router, public theme: ThemeService) {}

  onSubmit() {
    this.error.set('');
    this.loading.set(true);
    this.auth.login(this.username, this.password, this.pinStep() ? this.pin : undefined).subscribe({
      next: res => {
        if (res.pinRequired) {
          this.pinStep.set(true);
          this.loading.set(false);
          return;
        }
        this.router.navigate(['/admin/dashboard']);
      },
      error: (e) => {
        this.error.set(this.pinStep()
          ? (typeof e?.error === 'string' ? e.error : 'Nesprávny PIN.')
          : 'Nesprávne meno alebo heslo');
        this.loading.set(false);
      }
    });
  }

  backToPassword() {
    this.pinStep.set(false);
    this.pin = '';
    this.error.set('');
  }
}
