/** Back-office routes without path parameters (tenant owner with all modules). */
export const TENANT_ROUTES = [
  '/app/uebersicht', '/app/marktplatz', '/app/warenkorb', '/app/bestellungen/einkauf', '/app/bestellungen/verkauf',
  '/app/finanzen', '/app/finanzen/rechnungen', '/app/angebote', '/app/verkauf/rechnungen', '/app/verkauf/kassenbelege',
  '/app/lager', '/app/lager/geraete', '/app/importe', '/app/importe/neu', '/app/ankauf', '/app/ankauf/ankaeufe',
  '/app/berichte/tagesbericht', '/app/konto', '/app/unternehmen', '/app/team',
];

/** Platform-admin pages (tabs of the administration area). */
export const ADMIN_ROUTES = [
  '/app/administration', '/app/administration/benutzer', '/app/administration/marktplatz', '/app/administration/reklamationen',
  '/app/administration/ankauf', '/app/administration/ankauf/preisregeln', '/app/administration/rechnungen',
  '/app/administration/kassen-tse', '/app/administration/imei-sperrliste',
];

export const VIEWPORTS = [
  { name: 'phone', width: 375, height: 812 },
  { name: 'tablet', width: 820, height: 1180 },
  { name: 'desktop', width: 1440, height: 900 },
];
