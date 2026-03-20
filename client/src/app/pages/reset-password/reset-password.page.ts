import { Component, signal, OnInit } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-reset-password',
  imports: [FormsModule, RouterLink],
  templateUrl: './reset-password.page.html'
})
export class ResetPasswordPage implements OnInit {
  newPassword = '';
  confirmPassword = '';
  loading = signal(false);
  success = signal(false);
  error = signal('');

  private username = '';
  private token = '';

  constructor(private auth: AuthService, private route: ActivatedRoute) {}

  ngOnInit() {
    this.username = this.route.snapshot.queryParams['username'] ?? '';
    this.token = this.route.snapshot.queryParams['token'] ?? '';
  }

  get valid() {
    return this.newPassword.length >= 6 && this.newPassword === this.confirmPassword;
  }

  onSubmit() {
    if (!this.valid) return;
    this.error.set('');
    this.loading.set(true);
    this.auth.resetPassword(this.username, this.token, this.newPassword).subscribe({
      next: () => { this.success.set(true); this.loading.set(false); },
      error: (err) => {
        this.error.set(err.error ?? 'Odkaz je neplatný alebo vypršal.');
        this.loading.set(false);
      }
    });
  }
}
