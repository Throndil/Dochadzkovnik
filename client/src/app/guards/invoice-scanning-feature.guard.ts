import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { FeatureFlagService } from '../services/feature-flag.service';

/**
 * Blocks /admin/invoices unless the InvoiceScanning feature flag is on OR
 * the current user is the superadmin. Bounces back to the dashboard so a
 * regular admin who somehow lands on this URL doesn't see a 404 — they just
 * don't know the page exists. Same pattern as notificationsFeatureGuard.
 */
export const invoiceScanningFeatureGuard: CanActivateFn = () => {
  const flags = inject(FeatureFlagService);
  const auth = inject(AuthService);
  const router = inject(Router);

  if (flags.invoiceScanning() || auth.isSuperAdmin()) {
    return true;
  }

  router.navigate(['/admin/dashboard']);
  return false;
};
