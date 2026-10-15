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
  section: 'main' | 'purchasing' | 'sales' | 'settings';
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
  { path: '/app/finanzen', labelKey: 'nav.finance', module: 'B2BSales', permission: 'orders.fulfil', section: 'sales' },
  { path: '/app/angebote', labelKey: 'nav.offers', module: 'B2BSales', section: 'sales' },
  { path: '/app/lager', labelKey: 'nav.inventory', module: 'Inventory', section: 'sales' },
  { path: '/app/importe', labelKey: 'nav.imports', module: 'ProductImport', permission: 'imports.manage', section: 'sales' },
  { path: '/app/team', labelKey: 'nav.team', permission: 'organization.manage', always: true, section: 'settings' },
  { path: '/app/administration', labelKey: 'nav.admin', adminOnly: true, section: 'settings' },
];

export const NAV_SECTIONS: NavItem['section'][] = ['main', 'purchasing', 'sales', 'settings'];
