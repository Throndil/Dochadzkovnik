import { Injectable, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../environments/environment';
import { tap } from 'rxjs';

interface AuthResponse {
  token: string;
  displayName: string;
  /** True = password OK but the account requires a security PIN — no token
   *  yet; show the PIN step and call login again with the pin. */
  pinRequired?: boolean;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private tokenSignal = signal<string | null>(localStorage.getItem('token'));
  private displayNameSignal = signal<string>(localStorage.getItem('displayName') ?? '');

  isLoggedIn = computed(() => !!this.tokenSignal());
  displayName = computed(() => this.displayNameSignal());
  token = computed(() => this.tokenSignal());

  /**
   * True when the current JWT carries the isSuperAdmin=true claim.
   * Used to gate the Funkcie card on the Account page and the Notifikácie
   * navbar link / route. Reads the claim straight off the token rather than
   * making a server round-trip — token is signed, so claim cannot be forged.
   */
  isSuperAdmin = computed(() => {
    const token = this.tokenSignal();
    if (!token) return false;
    return decodeJwtClaim(token, 'isSuperAdmin') === 'true';
  });

  constructor(private http: HttpClient) {}

  login(username: string, password: string, pin?: string) {
    return this.http.post<AuthResponse>(`${environment.apiUrl}/auth/login`, { username, password, pin })
      .pipe(tap(res => {
        // The PIN-required half-response carries no token — persist nothing.
        if (!res.token) return;
        localStorage.setItem('token', res.token);
        localStorage.setItem('displayName', res.displayName);
        this.tokenSignal.set(res.token);
        this.displayNameSignal.set(res.displayName);
      }));
  }

  /** Set (currentPin null) or change (currentPin required) the security PIN. */
  changeAdminPin(currentPin: string | null, newPin: string) {
    return this.http.put(`${environment.apiUrl}/auth/admin-pin`, { currentPin, newPin });
  }

  /** Change the navbar display name; keeps the local session in sync. */
  updateDisplayName(displayName: string) {
    return this.http.put(`${environment.apiUrl}/auth/display-name`, { displayName })
      .pipe(tap(() => {
        localStorage.setItem('displayName', displayName);
        this.displayNameSignal.set(displayName);
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
    return this.http.get<{ email: string; userName: string; hasAdminPin: boolean }>(`${environment.apiUrl}/auth/me`);
  }

  logout() {
    localStorage.removeItem('token');
    localStorage.removeItem('displayName');
    this.tokenSignal.set(null);
    this.displayNameSignal.set('');
  }
}

/**
 * Decode a single claim out of a JWT without pulling in a dependency.
 * JWTs are three base64url-encoded segments separated by dots; the middle one
 * is the JSON payload. We don't verify the signature here — the server does
 * that on every protected request. Returns undefined on any parse failure.
 */
function decodeJwtClaim(token: string, claim: string): string | undefined {
  try {
    const parts = token.split('.');
    if (parts.length !== 3) return undefined;
    let payload = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    while (payload.length % 4 !== 0) payload += '=';
    const json = atob(payload);
    const obj = JSON.parse(json);
    const v = obj?.[claim];
    return v === undefined || v === null ? undefined : String(v);
  } catch {
    return undefined;
  }
}
