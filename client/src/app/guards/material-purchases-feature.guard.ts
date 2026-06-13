import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { FeatureFlagService } from '../services/feature-flag.service';

/**
 * Blocks the new admin Materiál → Nákupy / Neidentifikované routes (and any
 * other route specific to the Material Purchases feature) unless the
 * MaterialPurchases flag is on OR the current user is the superadmin.
 * Same pattern as notificationsFeatureGuard / commanderFeatureGuard:
 * bounce to the dashboard so a regular admin who somehow lands here doesn't
 * see a 404 — they just don't know the page exists.
 */
export const materialPurchasesFeatureGuard: CanActivateFn = () => {
  const flags = inject(FeatureFlagService);
  const auth = inject(AuthService);
  const router = inject(Router);

  if (flags.materialPurchases() || auth.isSuperAdmin()) {
    return true;
  }

  router.navigate(['/admin/dashboard']);
  return false;
};
