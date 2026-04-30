import { Injectable, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

/**
 * Loads the server-side feature-flag map (one flat object) and exposes it as
 * signals so templates and route guards can react with no manual subscribe.
 * Endpoint is anonymous, so the kiosk loads flags without a JWT.
 *
 * The known keys mirror what the backend seeds in Program.cs. To add a new
 * flag: insert it in Program.cs's seed loop AND add the matching signal here.
 */
@Injectable({ providedIn: 'root' })
export class FeatureFlagService {
  private notificationsSignal = signal(false);
  notifications = computed(() => this.notificationsSignal());

  constructor(private http: HttpClient) {}

  /**
   * Fetch the current state from the server and update local signals.
   * Called by the APP_INITIALIZER on app boot, and again after the superadmin
   * toggles a flag so UI re-renders without a refresh. Failures default to all
   * flags off — safer to under-show than to leak a hidden feature on a bad
   * network response.
   */
  async load(): Promise<void> {
    try {
      const flags = await firstValueFrom(
        this.http.get<Record<string, boolean>>(`${environment.apiUrl}/feature-flags`)
      );
      this.notificationsSignal.set(!!flags?.['notifications']);
    } catch {
      this.notificationsSignal.set(false);
    }
  }

  /**
   * Superadmin-only — flips a flag server-side and refreshes local state.
   */
  async setEnabled(key: string, enabled: boolean): Promise<void> {
    await firstValueFrom(
      this.http.put(`${environment.apiUrl}/feature-flags/${key}`, { enabled })
    );
    await this.load();
  }
}
