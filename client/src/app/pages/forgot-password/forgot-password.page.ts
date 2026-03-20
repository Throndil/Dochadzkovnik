import { Component, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-forgot-password',
  imports: [FormsModule, RouterLink],
  templateUrl: './forgot-password.page.html'
})
export class ForgotPasswordPage {
  username = '';
  sent = signal(false);
  loading = signal(false);

  constructor(private auth: AuthService) {}

  onSubmit() {
    this.loading.set(true);
    this.auth.forgotPassword(this.username).subscribe({
      next: () => { this.sent.set(true); this.loading.set(false); },
      error: () => { this.sent.set(true); this.loading.set(false); } // show same message to avoid enumeration
    });
  }
}
