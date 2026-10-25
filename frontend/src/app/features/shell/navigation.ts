import { ModuleName, Permission } from '../../core/api/models';

export interface NavItem {
  path: string;
  labelKey: string;
  module?: ModuleName;
  permission?: Permission;
  /** Visible regardless of verification state (not a module page). */
  always?: boolean;
  adminOnly?: boolean;
  badge?: 'cart';
  /** Active only on exactly this path (not on child paths that have their own entry). */
  exact?: boolean;
  /** Further path prefixes that also mark this entry active (e.g. detail pages under another path). */
  alsoActive?: string[];
  section: 'main' | 'purchasing' | 'sales' | 'pos' | 'platform' | 'settings';
}

/** Single source of truth for the side navigation; visibility mirrors the API's module/permission checks. */
export const NAV_ITEMS: NavItem[] = [
  { path: '/app/uebersicht', labelKey: 'nav.dashboard', always: true, section: 'main' },
  { path: '/app/marktplatz', labelKey: 'nav.marketplace', module: 'B2BPurchasing', permission: 'marketplace.browse', section: 'purchasing' },
  { path: '/app/warenkorb', labelKey: 'nav.cart', module: 'B2BPurchasing', permission: 'orders.place', badge: 'cart', section: 'purchasing' },
  { path: '/app/bestellungen/einkauf', labelKey: 'nav.purchases', module: 'B2BPurchasing', permission: 'orders.read', section: 'purchasing' },
  { path: '/app/ankauf', labelKey: 'nav.ankauf', module: 'Ankauf', exact: true, section: 'purchasing' },
  { path: '/app/ankauf/ankaeufe', labelKey: 'nav.intakes', module: 'Ankauf', permission: 'inventory.manage', section: 'purchasing' },
  { path: '/app/bestellungen/verkauf', labelKey: 'nav.sales', module: 'B2BSales', permission: 'orders.read', section: 'sales' },
  { path: '/app/finanzen', labelKey: 'nav.finance', module: 'B2BSales', permission: 'orders.fulfil', exact: true, section: 'sales' },
  { path: '/app/finanzen/rechnungen', labelKey: 'nav.invoices', module: 'B2BSales', permission: 'orders.fulfil', section: 'sales' },
  { path: '/app/angebote', labelKey: 'nav.offers', module: 'B2BSales', section: 'sales' },
  { path: '/app/lager', labelKey: 'nav.inventory', module: 'Inventory', section: 'sales' },
  { path: '/app/importe', labelKey: 'nav.imports', module: 'ProductImport', permission: 'imports.manage', section: 'sales' },
  { path: '/app/berichte/tagesbericht', labelKey: 'nav.dailyReport', permission: 'reports.read', always: true, section: 'main' },
  { path: '/app/team', labelKey: 'nav.team', permission: 'organization.manage', always: true, section: 'settings' },
  // Every member (also of a not yet verified company) sees the company data; editing needs organization.manage.
  { path: '/app/unternehmen', labelKey: 'nav.company', section: 'settings' },
  { path: '/app/administration', labelKey: 'admin.tabs.organizations', adminOnly: true, exact: true, alsoActive: ['/app/administration/unternehmen'], section: 'platform' },
  { path: '/app/administration/benutzer', labelKey: 'admin.tabs.users', adminOnly: true, section: 'platform' },
  { path: '/app/administration/marktplatz', labelKey: 'admin.tabs.moderation', adminOnly: true, section: 'platform' },
  { path: '/app/administration/reklamationen', labelKey: 'admin.tabs.disputes', adminOnly: true, section: 'platform' },
  { path: '/app/administration/ankauf', labelKey: 'admin.tabs.ankauf', adminOnly: true, section: 'platform' },
  { path: '/app/administration/rechnungen', labelKey: 'admin.tabs.invoices', adminOnly: true, section: 'platform' },
  { path: '/app/administration/kassen-tse', labelKey: 'admin.tabs.fiscal', adminOnly: true, section: 'platform' },
];

export const NAV_SECTIONS: NavItem['section'][] = ['main', 'purchasing', 'sales', 'pos', 'platform', 'settings'];
