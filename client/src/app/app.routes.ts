import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';
import { commanderFeatureGuard } from './guards/commander-feature.guard';
import { notificationsFeatureGuard } from './guards/notifications-feature.guard';
import { invoiceScanningFeatureGuard } from './guards/invoice-scanning-feature.guard';
import { invoiceCameraScanFeatureGuard } from './guards/invoice-camera-scan-feature.guard';
import { payrollAndPnLFeatureGuard } from './guards/payroll-and-pnl-feature.guard';

export const routes: Routes = [
  { path: '', redirectTo: 'kiosk', pathMatch: 'full' },
  {
    path: 'kiosk',
    loadComponent: () => import('./pages/kiosk/kiosk.page').then(m => m.KioskPage)
  },
  {
    path: 'login',
    loadComponent: () => import('./pages/login/login.page').then(m => m.LoginPage)
  },
  {
    path: 'forgot-password',
    loadComponent: () => import('./pages/forgot-password/forgot-password.page').then(m => m.ForgotPasswordPage)
  },
  {
    path: 'reset-password',
    loadComponent: () => import('./pages/reset-password/reset-password.page').then(m => m.ResetPasswordPage)
  },
  {
    path: 'admin',
    canActivate: [authGuard],
    children: [
      { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
      {
        path: 'dashboard',
        loadComponent: () => import('./pages/dashboard/dashboard.page').then(m => m.DashboardPage)
      },
      {
        path: 'employees',
        loadComponent: () => import('./pages/employees/employees.page').then(m => m.EmployeesPage)
      },
      {
        path: 'employees/:id',
        loadComponent: () => import('./pages/employee-detail/employee-detail.page').then(m => m.EmployeeDetailPage)
      },
      {
        path: 'locations',
        loadComponent: () => import('./pages/locations/locations.page').then(m => m.LocationsPage)
      },
      {
        path: 'locations/:id',
        loadComponent: () => import('./pages/location-detail/location-detail.page').then(m => m.LocationDetailPage)
      },
      {
        path: 'cars',
        loadComponent: () => import('./pages/cars/cars.page').then(m => m.CarsPage)
      },
      {
        path: 'materials',
        loadComponent: () => import('./pages/materials/materials.page').then(m => m.MaterialsPage)
      },
      {
        path: 'notifikacie',
        canActivate: [notificationsFeatureGuard],
        loadComponent: () => import('./pages/notifications/notifications.page').then(m => m.NotificationsPage)
      },
      {
        path: 'commander',
        canActivate: [commanderFeatureGuard],
        loadComponent: () => import('./pages/commander/commander.page').then(m => m.CommanderPage)
      },
      {
        path: 'invoices',
        canActivate: [invoiceScanningFeatureGuard],
        loadComponent: () => import('./pages/invoices/invoices.page').then(m => m.InvoicesPage)
      },
      {
        path: 'invoices/scan',
        canActivate: [invoiceCameraScanFeatureGuard],
        loadComponent: () => import('./pages/invoice-camera/invoice-camera.page').then(m => m.InvoiceCameraPage)
      },
      {
        path: 'invoices/:id',
        canActivate: [invoiceScanningFeatureGuard],
        loadComponent: () => import('./pages/invoice-review/invoice-review.page').then(m => m.InvoiceReviewPage)
      },
      {
        path: 'mzdy',
        canActivate: [payrollAndPnLFeatureGuard],
        loadComponent: () => import('./pages/mzdy/mzdy.page').then(m => m.MzdyPage)
      },
      {
        path: 'cars/:id',
        loadComponent: () => import('./pages/car-detail/car-detail.page').then(m => m.CarDetailPage)
      },
      {
        path: 'time-entries',
        loadComponent: () => import('./pages/time-entries/time-entries.page').then(m => m.TimeEntriesPage)
      },
      { path: 'reports', redirectTo: 'time-entries', pathMatch: 'full' },
      {
        path: 'account',
        loadComponent: () => import('./pages/account/account.page').then(m => m.AccountPage)
      }
    ]
  }
];
