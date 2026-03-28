import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';

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
