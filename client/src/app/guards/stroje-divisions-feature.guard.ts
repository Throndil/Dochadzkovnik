import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { FeatureFlagService } from '../services/feature-flag.service';

/**
 * Blocks the Stroje a divízie module pages (/admin/stroje) unless the
 * StrojeDivisions flag is on OR the current user is the superadmin.
 * Same pattern as plannerFeatureGuard.
 */
export const strojeDivisionsFeatureGuard: CanActivateFn = () => {
  const flags = inject(FeatureFlagService);
  const auth = inject(AuthService);
  const router = inject(Router);

  if (flags.strojeDivisions() || auth.isSuperAdmin()) {
    return true;
  }

  router.navigate(['/admin/dashboard']);
  return false;
};
