// Verifies that every static translation key used in templates/TS exists in de.json and en.json,
// that every key of de.json exists in en.json and in the other complete translations (tr, es, ru, ar, fa),
// that no translation has keys unknown to de.json, and that every translation keeps exactly the
// interpolation placeholders ({name}) of the German source. Usage: node scripts/check-i18n.mjs [--list]
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
  // Ankauf keys are referenced from TS logic (validators, blocked reasons) and error codes.
  /'((?:ankauf|ankaufAdmin)(?:\.\w+)+)'/g,
  /'(errors\.codes\.ankauf\.\w+)'/g,
  // Commission invoice keys are referenced from TS logic (validation, run outcome) as well.
  /'((?:invoices|invoiceKind)(?:\.\w+)+)'/g,
  /'(errors\.codes\.invoicing\.\w+)'/g,
  // POS keys are referenced from TS logic (error texts, validation, shortcut labels) as well.
  /'((?:pos|posPaymentMethod|receiptKind|taxTreatment|posSessionStatus|posRegisterStatus|cashMovementKind)(?:\.[A-Za-z0-9]+)+)'/g, // not API codes like pos.no_open_session
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
// Permission names and API codes that look like translation keys (e.g. 'pos.discount', error code 'pos.price').
const IGNORE = new Set(['pos.sell','pos.manage','pos.discount','pos.refund','pos.price','pos.reason','imports.manage','inventory.manage','offers.manage','orders.fulfil','orders.place','orders.read','cart.items']);
for (const k of IGNORE) used.delete(k);
const missingDe = [...used].filter((k) => !(k in de)).sort();
const missingEn = Object.keys(de).filter((k) => !(k in en)).sort();
const extraEn = Object.keys(en).filter((k) => !(k in de)).sort();
if (missingDe.length) console.log('Missing in de.json:\n  ' + missingDe.join('\n  '));
if (missingEn.length) console.log('Missing in en.json:\n  ' + missingEn.join('\n  '));
if (extraEn.length) console.log('Only in en.json:\n  ' + extraEn.join('\n  '));

// Complete translations besides English: every de.json key must exist, no unknown keys.
const FULL = ['tr', 'es', 'ru', 'ar', 'fa'];
const others = Object.fromEntries(FULL.map((l) => [l, loc(l)]));
let failed = missingDe.length > 0 || missingEn.length > 0;
for (const [l, dict] of Object.entries(others)) {
  const missing = Object.keys(de).filter((k) => !(k in dict)).sort();
  const extra = Object.keys(dict).filter((k) => !(k in de)).sort();
  if (missing.length) console.log(`Missing in ${l}.json (${missing.length}):\n  ` + missing.join('\n  '));
  if (extra.length) console.log(`Only in ${l}.json:\n  ` + extra.join('\n  '));
  if (missing.length || extra.length) failed = true;
}

// Placeholders: each translation must use exactly the placeholders of the German text, and must not be empty.
const placeholders = (text) => [...new Set([...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]))].sort().join(',');
for (const [l, dict] of Object.entries({ en, ...others })) {
  const bad = Object.keys(de).filter((k) => k in dict && placeholders(dict[k]) !== placeholders(de[k]));
  for (const k of bad) console.log(`Placeholder mismatch in ${l}.json ${k}: de {${placeholders(de[k])}} vs ${l} {${placeholders(dict[k])}}`);
  const empty = Object.keys(dict).filter((k) => k in de && de[k].trim() !== '' && dict[k].trim() === '');
  for (const k of empty) console.log(`Empty translation in ${l}.json: ${k}`);
  if (bad.length || empty.length) failed = true;
}

console.log(
  `${used.size} keys used, de: ${Object.keys(de).length}, en: ${Object.keys(en).length}, ` +
    FULL.map((l) => `${l}: ${Object.keys(others[l]).length}`).join(', '),
);
process.exit(failed ? 1 : 0);
