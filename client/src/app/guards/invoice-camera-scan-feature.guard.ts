import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { FeatureFlagService } from '../services/feature-flag.service';

/**
 * Blocks /admin/invoices/scan unless BOTH the InvoiceScanning AND the
 * InvoiceCameraScan flags are on, OR the current user is the superadmin.
 * Camera scan is a sub-feature of invoice scanning, so the parent flag
 * must be on too.
 */
export const invoiceCameraScanFeatureGuard: CanActivateFn = () => {
  const flags = inject(FeatureFlagService);
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isSuperAdmin()) return true;
  if (flags.invoiceScanning() && flags.invoiceCameraScan()) return true;

  router.navigate(['/admin/invoices']);
  return false;
};
