import { inject } from '@angular/core';
import { Router, type CanActivateFn } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { FeatureFlagService } from '../services/feature-flag.service';

/**
 * Blocks /admin/planner unless the Planner feature flag is on OR the current
 * user is the superadmin. Same pattern as notificationsFeatureGuard.
 */
export const plannerFeatureGuard: CanActivateFn = () => {
  const flags = inject(FeatureFlagService);
  const auth = inject(AuthService);
  const router = inject(Router);

  if (flags.planner() || auth.isSuperAdmin()) {
    return true;
  }

  router.navigate(['/admin/dashboard']);
  return false;
};
