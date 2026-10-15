// Verifies that every static translation key used in templates/TS exists in de.json and en.json,
// and that every key of de.json exists in en.json. Usage: node scripts/check-i18n.mjs [--list]
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const root = new URL('../src/app', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
const files = [];
(function walk(dir) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p);
    else if (/\.(ts|html)$/.test(name) && !name.endsWith('.spec.ts')) files.push(p);
  }
})(root);

const used = new Set();
const patterns = [
  /'([a-zA-Z][\w]*(?:\.[\w]+)+)'\s*\|\s*t\b/g,
  /\bt\(\s*'([a-zA-Z][\w]*(?:\.[\w]+)+)'/g,
  /labelKey:\s*'([\w.]+)'/g,
  /successKey[^']*'([\w.]+)'/g,
  /'(orders\.(?:confirmed|shipped|rejected|cancelled)|admin\.(?:verified|rejected|suspended|modulesSaved))'/g,
  /\(?'((?:dashboard|orders|offers|cart|imports|common|admin|inventory|team)\.[\w.]+)'\)?\s*(?:\||:|\))/g,
  // Shipping keys are also referenced from TS maps and validators (e.g. `errors.weightKg = 'shipping.form.weightInvalid'`).
  /'(shipping(?:\.\w+)+)'/g,
  // Dispute keys are referenced from TS maps and validators as well (e.g. `'disputes.validation.required'`).
  /'((?:disputes|disputeReason|disputeStatus|disputeParty)(?:\.\w+)+)'/g,
  /'(admin\.(?:tabs|disputes)(?:\.\w+)+)'/g,
];
for (const f of files) {
  const src = readFileSync(f, 'utf8');
  for (const re of patterns) for (const m of src.matchAll(re)) used.add(m[1]);
}

function flatten(obj, prefix = '', out = {}) {
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === 'string') out[key] = v;
    else flatten(v, key, out);
  }
  return out;
}
const loc = (l) => flatten(JSON.parse(readFileSync(join(root, 'core/i18n/locales', `${l}.json`), 'utf8')));
const de = loc('de');
const en = loc('en');

if (process.argv.includes('--list')) console.log([...used].sort().join('\n'));
const IGNORE = new Set(['imports.manage','inventory.manage','offers.manage','orders.fulfil','orders.place','orders.read','cart.items']);
for (const k of IGNORE) used.delete(k);
const missingDe = [...used].filter((k) => !(k in de)).sort();
const missingEn = Object.keys(de).filter((k) => !(k in en)).sort();
const extraEn = Object.keys(en).filter((k) => !(k in de)).sort();
if (missingDe.length) console.log('Missing in de.json:\n  ' + missingDe.join('\n  '));
if (missingEn.length) console.log('Missing in en.json:\n  ' + missingEn.join('\n  '));
if (extraEn.length) console.log('Only in en.json:\n  ' + extraEn.join('\n  '));
console.log(`${used.size} keys used, de: ${Object.keys(de).length}, en: ${Object.keys(en).length}`);
process.exit(missingDe.length || missingEn.length ? 1 : 0);
