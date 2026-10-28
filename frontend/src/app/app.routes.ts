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
    path: 'passwort-vergessen',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/auth/forgot-password.page').then((m) => m.ForgotPasswordPage),
  },
  // Targets of the email links (token in the URL fragment); open signed in or not.
  {
    path: 'passwort-zuruecksetzen',
    loadComponent: () => import('./features/auth/reset-password.page').then((m) => m.ResetPasswordPage),
  },
  {
    path: 'email-bestaetigen',
    loadComponent: () => import('./features/auth/verify-email.page').then((m) => m.VerifyEmailPage),
  },
  // Public, read-only POS receipt (QR link on the receipt screen); no login.
  {
    path: 'beleg/:token',
    loadComponent: () => import('./features/pos/public-receipt.page').then((m) => m.PublicReceiptPage),
  },
  // Public Ankauf self check-in (QR at the counter, short-lived single-use link); no login.
  {
    path: 'checkin/:token',
    loadComponent: () => import('./features/ankauf/public-checkin.page').then((m) => m.PublicCheckInPage),
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
        path: 'finanzen/rechnungen',
        canActivate: [accessGuard('B2BSales', 'orders.fulfil')],
        loadComponent: () => import('./features/finance/invoices/invoices.page').then((m) => m.InvoicesPage),
      },
      {
        path: 'finanzen/rechnungen/:id',
        canActivate: [accessGuard('B2BSales', 'orders.fulfil')],
        loadComponent: () => import('./features/finance/invoices/invoice-detail.page').then((m) => m.InvoiceDetailPage),
      },
      {
        path: 'angebote',
        canActivate: [accessGuard('B2BSales')],
        loadComponent: () => import('./features/offers/offers.page').then((m) => m.OffersPage),
      },
      // Verkauf: quotations and invoices outside the till (module Pos, pos.sell).
      { path: 'verkauf', pathMatch: 'full', redirectTo: 'verkauf/rechnungen' },
      ...salesRoutes('verkauf/angebote', 'quotation', false).map((r) => ({ ...r, canActivate: [accessGuard('Pos', 'pos.sell')] })),
      ...salesRoutes('verkauf/rechnungen', 'invoice', false).map((r) => ({ ...r, canActivate: [accessGuard('Pos', 'pos.sell')] })),
      {
        path: 'verkauf/kassenbelege',
        canActivate: [accessGuard('Pos', 'pos.sell')],
        loadComponent: () => import('./features/sales/sales-receipts.page').then((m) => m.SalesReceiptsPage),
      },
      {
        path: 'lager',
        canActivate: [accessGuard('Inventory')],
        loadComponent: () => import('./features/inventory/inventory.page').then((m) => m.InventoryPage),
      },
      {
        path: 'lager/geraete',
        canActivate: [accessGuard('Inventory', 'inventory.manage')],
        loadComponent: () => import('./features/inventory/units.page').then((m) => m.UnitsPage),
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
        path: 'ankauf',
        canActivate: [accessGuard('Ankauf')],
        loadComponent: () => import('./features/ankauf/ankauf.page').then((m) => m.AnkaufPage),
      },
      {
        path: 'ankauf/ankaeufe',
        canActivate: [accessGuard('Ankauf', 'inventory.manage')],
        loadComponent: () => import('./features/ankauf/intakes.page').then((m) => m.IntakesPage),
      },
      {
        path: 'ankauf/ankaeufe/neu/:quoteId',
        canActivate: [accessGuard('Ankauf', 'inventory.manage')],
        loadComponent: () => import('./features/ankauf/intake.page').then((m) => m.IntakePage),
      },
      {
        path: 'ankauf/ankaeufe/:id',
        canActivate: [accessGuard('Ankauf', 'inventory.manage')],
        loadComponent: () => import('./features/ankauf/intake.page').then((m) => m.IntakePage),
      },
      {
        path: 'berichte/tagesbericht',
        canActivate: [accessGuard(undefined, 'reports.read')],
        loadComponent: () => import('./features/reports/daily-report.page').then((m) => m.DailyReportPage),
      },
      // The POS moved to the standalone app under /kasse (old bookmarks keep working).
      { path: 'kasse', pathMatch: 'full', redirectTo: '/kasse' },
      { path: 'kasse/belege/:id', redirectTo: '/kasse/belege/:id' },
      { path: 'kasse/sitzungen/:id', redirectTo: '/kasse/sitzungen/:id' },
      { path: 'kasse/:registerId', redirectTo: '/kasse/:registerId' },
      { path: 'kasse/:registerId/belege', redirectTo: '/kasse/:registerId/belege' },
      { path: 'kasse/:registerId/sitzungen', redirectTo: '/kasse/:registerId/sitzungen' },
      {
        path: 'konto',
        loadComponent: () => import('./features/account/account.page').then((m) => m.AccountPage),
      },
      {
        path: 'unternehmen',
        loadComponent: () => import('./features/company/company.page').then((m) => m.CompanyPage),
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
        path: 'administration/unternehmen/:id',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/management/admin-company-detail.page').then((m) => m.AdminCompanyDetailPage),
      },
      {
        path: 'administration/benutzer',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/management/admin-users.page').then((m) => m.AdminUsersPage),
      },
      {
        path: 'administration/benutzer/:id',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/management/admin-user-detail.page').then((m) => m.AdminUserDetailPage),
      },
      {
        path: 'administration/reklamationen',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin-disputes.page').then((m) => m.AdminDisputesPage),
      },
      {
        path: 'administration/rechnungen',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin-invoices.page').then((m) => m.AdminInvoicesPage),
      },
      {
        path: 'administration/imei-sperrliste',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin-imei-blacklist.page').then((m) => m.AdminImeiBlacklistPage),
      },
      {
        path: 'administration/kassen-tse',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin-fiscal.page').then((m) => m.AdminFiscalPage),
      },
      {
        path: 'administration/reklamationen/:id',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/admin-dispute-detail.page').then((m) => m.AdminDisputeDetailPage),
      },
      {
        path: 'administration/marktplatz',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/moderation/admin-moderation.page').then((m) => m.AdminModerationPage),
      },
      {
        path: 'administration/ankauf',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/ankauf/admin-price-lists.page').then((m) => m.AdminPriceListsPage),
      },
      {
        path: 'administration/ankauf/preisregeln',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/ankauf/admin-policies.page').then((m) => m.AdminPoliciesPage),
      },
      {
        path: 'administration/ankauf/preislisten/:id',
        canActivate: [platformAdminGuard],
        loadComponent: () => import('./features/admin/ankauf/admin-price-list-review.page').then((m) => m.AdminPriceListReviewPage),
      },
    ],
  },
  {
    // Standalone, full-screen POS app (not inside the back-office shell). Guards run after authGuard has loaded /api/me.
    path: 'kasse',
    canActivate: [authGuard],
    loadComponent: () => import('./features/pos/pos-app').then((m) => m.PosAppComponent),
    children: [
      {
        path: '',
        canActivateChild: [accessGuard('Pos', 'pos.sell')],
        children: [
          { path: '', pathMatch: 'full', loadComponent: () => import('./features/pos/registers.page').then((m) => m.PosRegistersPage) },
          { path: 'belege/:id', loadComponent: () => import('./features/pos/receipt-detail.page').then((m) => m.PosReceiptDetailPage) },
          { path: 'sitzungen/:id', loadComponent: () => import('./features/pos/session.page').then((m) => m.PosSessionPage) },
          { path: 'berichte', loadComponent: () => import('./features/pos/report.page').then((m) => m.PosReportPage) },
          // Quotations and invoices outside the till (shared with the back office, ADR-0014).
          ...salesRoutes(':registerId/angebote', 'quotation', true),
          ...salesRoutes(':registerId/rechnungen', 'invoice', true),
          {
            path: 'berichte/tag',
            canActivate: [accessGuard(undefined, 'reports.read')],
            loadComponent: () => import('./features/reports/daily-report.page').then((m) => m.DailyReportPage),
          },
          // Ankauf inside the POS app (Petrol design; the register is preselected for cash payouts).
          {
            path: ':registerId/ankauf',
            canActivate: [accessGuard('Ankauf')],
            loadComponent: () => import('./features/ankauf/ankauf.page').then((m) => m.AnkaufPage),
          },
          {
            path: ':registerId/ankauf/ankaeufe',
            canActivate: [accessGuard('Ankauf', 'inventory.manage')],
            loadComponent: () => import('./features/ankauf/intakes.page').then((m) => m.IntakesPage),
          },
          {
            path: ':registerId/ankauf/ankaeufe/neu/:quoteId',
            canActivate: [accessGuard('Ankauf', 'inventory.manage')],
            loadComponent: () => import('./features/ankauf/intake.page').then((m) => m.IntakePage),
          },
          {
            path: ':registerId/ankauf/ankaeufe/:id',
            canActivate: [accessGuard('Ankauf', 'inventory.manage')],
            loadComponent: () => import('./features/ankauf/intake.page').then((m) => m.IntakePage),
          },
          { path: ':registerId', loadComponent: () => import('./features/pos/till.page').then((m) => m.TillPage) },
          { path: ':registerId/belege', loadComponent: () => import('./features/pos/receipts.page').then((m) => m.PosReceiptsPage) },
          { path: ':registerId/sitzungen', loadComponent: () => import('./features/pos/sessions.page').then((m) => m.PosSessionsPage) },
          { path: ':registerId/preise', loadComponent: () => import('./features/pos/prices.page').then((m) => m.PosPricesPage) },
          { path: ':registerId/drucker', loadComponent: () => import('./features/pos/printing/printers.page').then((m) => m.PosPrintersPage) },
        ],
      },
    ],
  },
  { path: '**', redirectTo: 'app' },
];

/** List, new, detail and edit routes of quotations or invoices (POS app or back office). */
function salesRoutes(base: string, doc: 'quotation' | 'invoice', pos: boolean): Routes {
  const kind = doc === 'quotation' ? 'quotations' : 'invoices';
  const pages = () => import('./features/sales/sales-pages');
  return [
    { path: base, data: { kind }, loadComponent: () => pages().then((m) => (pos ? m.PosSalesListPage : m.SalesListPage)) },
    { path: `${base}/neu`, data: { doc }, loadComponent: () => pages().then((m) => (pos ? m.PosSalesFormPage : m.SalesFormPage)) },
    { path: `${base}/:id`, data: { doc }, loadComponent: () => pages().then((m) => (pos ? m.PosSalesDetailPage : m.SalesDetailPage)) },
    { path: `${base}/:id/bearbeiten`, data: { doc }, loadComponent: () => pages().then((m) => (pos ? m.PosSalesFormPage : m.SalesFormPage)) },
  ];
}
