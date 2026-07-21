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

  private commanderIntegrationSignal = signal(false);
  commanderIntegration = computed(() => this.commanderIntegrationSignal());

  private materialPurchasesSignal = signal(false);
  materialPurchases = computed(() => this.materialPurchasesSignal());

  private proofOfWorkChoicesSignal = signal(false);
  proofOfWorkChoices = computed(() => this.proofOfWorkChoicesSignal());

  private invoiceScanningSignal = signal(false);
  invoiceScanning = computed(() => this.invoiceScanningSignal());

  private invoiceCameraScanSignal = signal(false);
  invoiceCameraScan = computed(() => this.invoiceCameraScanSignal());

  private payrollAndPnLSignal = signal(false);
  payrollAndPnL = computed(() => this.payrollAndPnLSignal());

  private plannerSignal = signal(false);
  planner = computed(() => this.plannerSignal());

  private vehiclesSignal = signal(false);
  vehicles = computed(() => this.vehiclesSignal());

  private strojeDivisionsSignal = signal(false);
  strojeDivisions = computed(() => this.strojeDivisionsSignal());

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
      this.commanderIntegrationSignal.set(!!flags?.['commanderIntegration']);
      this.materialPurchasesSignal.set(!!flags?.['materialPurchases']);
      this.proofOfWorkChoicesSignal.set(!!flags?.['proofOfWorkChoices']);
      this.invoiceScanningSignal.set(!!flags?.['invoiceScanning']);
      this.invoiceCameraScanSignal.set(!!flags?.['invoiceCameraScan']);
      this.payrollAndPnLSignal.set(!!flags?.['payrollAndPnL']);
      this.plannerSignal.set(!!flags?.['planner']);
      this.vehiclesSignal.set(!!flags?.['vehicles']);
      this.strojeDivisionsSignal.set(!!flags?.['strojeDivisions']);
    } catch {
      this.notificationsSignal.set(false);
      this.commanderIntegrationSignal.set(false);
      this.materialPurchasesSignal.set(false);
      this.proofOfWorkChoicesSignal.set(false);
      this.invoiceScanningSignal.set(false);
      this.invoiceCameraScanSignal.set(false);
      this.payrollAndPnLSignal.set(false);
      this.plannerSignal.set(false);
      this.vehiclesSignal.set(false);
      this.strojeDivisionsSignal.set(false);
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
