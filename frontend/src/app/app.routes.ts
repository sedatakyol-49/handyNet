import { Routes } from '@angular/router';
import { accessGuard, authGuard, guestGuard, platformAdminGuard } from './core/auth/guards';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'app' },
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage),
  },
  {
    path: 'registrierung',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/register.page').then((m) => m.RegisterPage),
  },
  {
    path: 'app',
    canActivate: [authGuard],
    loadComponent: () => import('./features/shell/shell').then((m) => m.ShellComponent),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'uebersicht' },
      {
        path: 'uebersicht',
        loadComponent: () => import('./features/dashboard/dashboard.page').then((m) => m.DashboardPage),
      },
      {
        path: 'marktplatz',
        canActivate: [accessGuard('B2BPurchasing', 'marketplace.browse')],
        loadComponent: () => import('./features/marketplace/marketplace.page').then((m) => m.MarketplacePage),
      },
      {
        path: 'warenkorb',
        canActivate: [accessGuard('B2BPurchasing', 'orders.place')],
        loadComponent: () => import('./features/cart/cart.page').then((m) => m.CartPage),
      },
      {
        path: 'bestellungen/einkauf',
        canActivate: [accessGuard('B2BPurchasing', 'orders.read')],
        data: { role: 'buyer' },
        loadComponent: () => import('./features/orders/orders.page').then((m) => m.OrdersPage),
      },
      {
        path: 'bestellungen/verkauf',
        canActivate: [accessGuard('B2BSales', 'orders.read')],
        data: { role: 'sales' },
        loadComponent: () => import('./features/orders/orders.page').then((m) => m.OrdersPage),
      },
      {
        path: 'bestellungen/:id',
        canActivate: [accessGuard(undefined, 'orders.read')],
        loadComponent: () => import('./features/orders/order-detail.page').then((m) => m.OrderDetailPage),
      },
      {
        path: 'finanzen',
        canActivate: [accessGuard('B2BSales', 'orders.fulfil')],
        loadComponent: () => import('./features/finance/finance.page').then((m) => m.FinancePage),
      },
      {
        path: 'angebote',
        canActivate: [accessGuard('B2BSales')],
        loadComponent: () => import('./features/offers/offers.page').then((m) => m.OffersPage),
      },
      {
        path: 'lager',
        canActivate: [accessGuard('Inventory')],
        loadComponent: () => import('./features/inventory/inventory.page').then((m) => m.InventoryPage),
      },
      {
        path: 'importe',
        canActivate: [accessGuard('ProductImport', 'imports.manage')],
        loadComponent: () => import('./features/imports/imports-history.page').then((m) => m.ImportsHistoryPage),
      },
      {
        path: 'importe/neu',
        canActivate: [accessGuard('ProductImport', 'imports.manage')],
        loadComponent: () => import('./features/imports/import-wizard.page').then((m) => m.ImportWizardPage),
      },
      {
        path: 'importe/:id',
        canActivate: [accessGuard('ProductImport', 'imports.manage')],
        loadComponent: () => import('./features/imports/import-wizard.page').then((m) => m.ImportWizardPage),
      },
      {
        path: 'team',
        canActivate: [accessGuard(undefined, 'organization.manage')],
        loadComponent: () => import('./features/team/team.page').then((m) => m.TeamPage),
      },
      {
        path: 'administration',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin.page').then((m) => m.AdminPage),
      },
      {
        path: 'administration/reklamationen',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin-disputes.page').then((m) => m.AdminDisputesPage),
      },
      {
        path: 'administration/reklamationen/:id',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin-dispute-detail.page').then((m) => m.AdminDisputeDetailPage),
      },
    ],
  },
  { path: '**', redirectTo: 'app' },
];
