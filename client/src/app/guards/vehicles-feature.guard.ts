import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { FeatureFlagService } from '../services/feature-flag.service';

/**
 * Blocks the Vozidlá module pages (/admin/cars, /admin/palivove-karty) unless
 * the Vehicles flag is on OR the current user is the superadmin. The cars API
 * itself stays core (kiosk pick, time entries) — this only hides the
 * management surfaces. Same pattern as plannerFeatureGuard.
 */
export const vehiclesFeatureGuard: CanActivateFn = () => {
  const flags = inject(FeatureFlagService);
  const auth = inject(AuthService);
  const router = inject(Router);

  if (flags.vehicles() || auth.isSuperAdmin()) {
    return true;
  }

  router.navigate(['/admin/dashboard']);
  return false;
};
