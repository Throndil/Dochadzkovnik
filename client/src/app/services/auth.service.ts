import { Injectable, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';
import { tap } from 'rxjs';

interface AuthResponse {
  token: string;
  displayName: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private tokenSignal = signal<string | null>(localStorage.getItem('token'));
  private displayNameSignal = signal<string>(localStorage.getItem('displayName') ?? '');

  isLoggedIn = computed(() => !!this.tokenSignal());
  displayName = computed(() => this.displayNameSignal());
  token = computed(() => this.tokenSignal());

  constructor(private http: HttpClient) {}

  login(username: string, password: string) {
    return this.http.post<AuthResponse>(`${environment.apiUrl}/auth/login`, { username, password })
      .pipe(tap(res => {
        localStorage.setItem('token', res.token);
        localStorage.setItem('displayName', res.displayName);
        this.tokenSignal.set(res.token);
        this.displayNameSignal.set(res.displayName);
      }));
  }

  forgotPassword(username: string) {
    return this.http.post(`${environment.apiUrl}/auth/forgot-password`, { username });
  }

  resetPassword(username: string, token: string, newPassword: string) {
    return this.http.post(`${environment.apiUrl}/auth/reset-password`, { username, token, newPassword });
  }

  changePassword(currentPassword: string, newPassword: string) {
    return this.http.post(`${environment.apiUrl}/auth/change-password`, { currentPassword, newPassword });
  }

  updateRecoveryEmail(email: string) {
    return this.http.put(`${environment.apiUrl}/auth/recovery-email`, { email });
  }

  getMe() {
    return this.http.get<{ email: string }>(`${environment.apiUrl}/auth/me`);
  }

  logout() {
    localStorage.removeItem('token');
    localStorage.removeItem('displayName');
    this.tokenSignal.set(null);
    this.displayNameSignal.set('');
  }
}
