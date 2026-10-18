// Translation quality report for the locale files (complements check-i18n.mjs, which checks keys and placeholders).
//
// Per locale it reports
//   1. untranslated values: identical to the German source (all locales) or to English (tr, es, ru, ar, fa), except
//      values that are legitimately the same everywhere (codes, units, brand and product names, autonyms, keys listed
//      in scripts/i18n-quality-allow.json);
//   2. placeholder/token problems: {params} that differ from de, lost "§25a", lost keyboard keys (F2, F9, Esc, …) and
//      numbers of the German text that are missing in the translation (e.g. "19 %");
//   3. stale translations (best effort, from git history): keys whose German text changed in a later commit than the
//      translation, e.g. pos "nicht verfügbar" → "ausverkauft" while tr still says "mevcut değil";
//   4. script problems: Arabic/Persian values without Arabic script, Russian values without Cyrillic.
//
// Usage: node scripts/check-i18n-quality.mjs [--locale tr] [--json] [--no-git] [--warn-only]
// Exit code 1 when findings of category 1–4 exist (unless --warn-only).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const localesDir = join(here, '../src/app/core/i18n/locales');
const allowFile = join(here, 'i18n-quality-allow.json');
const args = process.argv.slice(2);
const only = args.includes('--locale') ? args[args.indexOf('--locale') + 1] : null;
const asJson = args.includes('--json');
const warnOnly = args.includes('--warn-only');
const useGit = !args.includes('--no-git');

const ALL = ['en', 'tr', 'es', 'ru', 'ar', 'fa'];
const NON_EN = ['tr', 'es', 'ru', 'ar', 'fa'];
const locales = only ? [only] : ALL;

function flatten(obj, prefix = '', out = {}) {
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (typeof v === 'string') out[key] = v;
    else if (v && typeof v === 'object') flatten(v, key, out);
  }
  return out;
}
const read = (l) => flatten(JSON.parse(readFileSync(join(localesDir, `${l}.json`), 'utf8')));
const de = read('de');
const en = read('en');
const dicts = Object.fromEntries(ALL.map((l) => [l, l === 'en' ? en : read(l)]));

// ------------------------------------------------------------------ allow-list
const allow = existsSync(allowFile) ? JSON.parse(readFileSync(allowFile, 'utf8')) : {};
const allowedKey = (locale, key) => {
  const patterns = [...(allow['*'] ?? []), ...(allow[locale] ?? [])];
  return patterns.some((p) => (p.endsWith('*') ? key.startsWith(p.slice(0, -1)) : key === p));
};
// Words that are the same in every language of the app (brands, codes, units, technical terms used as-is).
const SAME_WORDS = new Set(
  (allow.words ?? []).concat([
    'HandyNet', 'Apple', 'Samsung', 'Google', 'Xiaomi', 'iPhone', 'Galaxy', 'Pixel', 'DHL', 'DPD', 'GLS', 'UPS',
    'Hermes', 'EAN', 'GTIN', 'MPN', 'SKU', 'IMEI', 'IBAN', 'BIC', 'PDF', 'CSV', 'XLSX', 'TSE', 'POS', 'B2B', 'USt-IdNr.',
    'VAT', 'ID', 'OK', 'Logo', 'E-Mail', 'Online', 'Offline', 'PIN', 'Z', 'EK', 'VK', 'MwSt.', 'kg', 'cm', 'g', 'mm', '€',
    '%', 'Deutsch', 'English', 'Türkçe', 'Español', 'Русский', 'العربية', 'فارسی', 'Esc', 'Enter', 'Tab', 'F2', 'F4',
    'F8', 'F9', 'A', 'B', 'C', '§25a', 'HN', 'URL', 'API', 'Stripe', 'PayPal', 'SEPA', 'Excel', 'JPEG', 'PNG', 'WebP',
  ]),
);

/** True for values that need no translation: only placeholders, numbers, symbols, codes or allowed words. */
function exempt(value) {
  let v = value.replace(/\{\w+\}/g, ' ').trim();
  if (!/\p{L}/u.test(v)) return true; // punctuation, numbers, symbols only
  const words = v.split(/[\s/·•,;:()–—\-+|]+/).filter(Boolean);
  if (words.every((w) => SAME_WORDS.has(w) || /^[\p{Lu}\d.§%€#]+$/u.test(w) || !/\p{L}/u.test(w))) return true;
  return false;
}

// ------------------------------------------------------------------ token checks
const params = (t) => [...new Set([...t.matchAll(/\{(\w+)\}/g)].map((m) => m[1]))].sort().join(',');
// Keyboard keys must survive translation; localized key names count as the same key (Spanish "Intro" = Enter).
const KEYS = /(?<![\p{L}\d])(F\d{1,2}|Esc|Enter|Intro|Tab|Shift|Strg|Ctrl|Alt)(?![\p{L}\d])/gu;
const KEY_ALIASES = { Strg: 'Ctrl', Intro: 'Enter' };
const keysOf = (t) => [...new Set([...t.matchAll(KEYS)].map((m) => KEY_ALIASES[m[1]] ?? m[1]))].sort().join(',');
// Numbers as digit strings: thousands/decimal separators differ per language ("5.000", "5 000", "5,000", "5000").
const numbersOf = (t) =>
  [...t.replace(/\{\w+\}/g, '').matchAll(/\d{1,3}(?:[.,\s  ٬]\d{3})+(?!\d)|\d+(?:[.,٫]\d+)?/g)]
    .map((m) => m[0].replace(/\D/g, ''))
    .sort()
    .join(',');
// Native digits (Arabic-Indic / Persian) count as the same number.
const nativeDigits = (t) =>
  t.replace(/[٠-٩]/g, (d) => String(d.charCodeAt(0) - 0x660)).replace(/[۰-۹]/g, (d) => String(d.charCodeAt(0) - 0x6f0));

// ------------------------------------------------------------------ git history (stale translations)
function gitHistory() {
  const repoRoot = execFileSync('git', ['rev-parse', '--show-toplevel'], { cwd: here, encoding: 'utf8' }).trim();
  const rel = relative(repoRoot, localesDir).replace(/\\/g, '/');
  const shas = execFileSync('git', ['log', '--format=%H', '--reverse', '--', rel], { cwd: repoRoot, encoding: 'utf8' })
    .split('\n')
    .filter(Boolean);
  const show = (sha, l) => {
    try {
      return flatten(JSON.parse(execFileSync('git', ['show', `${sha}:${rel}/${l}.json`], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 << 20, stdio: ['ignore', 'pipe', 'ignore'] })));
    } catch {
      return null;
    }
  };
  // Snapshots: every commit that touched a locale file, then the working tree (index = shas.length).
  const snaps = shas.map((sha) => ({ sha, de: show(sha, 'de'), ...Object.fromEntries(ALL.map((l) => [l, show(sha, l)])) }));
  snaps.push({ sha: 'WORKTREE', de, ...dicts });
  /** Index of the snapshot where the value of `key` in `l` last changed (or first appeared). */
  const lastChange = (l, key) => {
    let idx = -1;
    let prev;
    snaps.forEach((s, i) => {
      const v = s[l]?.[key];
      if (v !== undefined && v !== prev) idx = i;
      prev = v;
    });
    return idx;
  };
  return { snaps, lastChange };
}

// ------------------------------------------------------------------ backend coverage (error codes, enums)
const backendDir = join(here, '../../backend/src');
const appDir = join(here, '../src/app');
function walkFiles(dir, re, out = []) {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) {
      if (!/^(bin|obj|node_modules|Migrations)$/.test(name)) walkFiles(p, re, out);
    } else if (re.test(name)) out.push(p);
  }
  return out;
}
// Codes the UI never shows: internal ledger invariants, refresh-token failures (the interceptor signs out), admin-only
// plumbing.
const CODE_IGNORE = [/^ledger\./, /^auth\.refresh_token_/, /^image\.admin_with_tenant$/];
// tEnum / i18n.enum prefix → backend enum (optionally "Module:Enum" when the name is ambiguous).
const ENUMS = {
  category: 'ProductCategory', quality: 'PartQuality', condition: 'OfferCondition', deviceCondition: 'DeviceCondition',
  deviceDefect: 'DeviceDefect', offerStatus: 'OfferStatus', orderStatus: 'OrderStatus', orgStatus: 'OrganizationStatus',
  disputeReason: 'DisputeReason', disputeStatus: 'DisputeStatus', disputeParty: 'DisputeParty', role: 'MembershipRole',
  carrier: 'Carrier', shipmentStatus: 'ShipmentStatus', trackingKind: 'TrackingKind', paymentStatus: 'PaymentStatus',
  paymentProvider: 'PaymentProvider', importStatus: 'ImportStatus', rowStatus: 'RowStatus', productAction: 'Imports:ProductAction',
  offerAction: 'Imports:OfferAction', movementType: 'MovementType', module: 'PlatformModule', priceListStatus: 'PriceListStatus',
  priceChange: 'PriceChange', entryDecision: 'EntryDecision', intakeStatus: 'IntakeStatus', idDocumentType: 'IdDocumentType',
  intakePaymentMethod: 'Ankauf:PaymentMethod', posPaymentMethod: 'Pos:PaymentMethod', receiptKind: 'ReceiptKind',
  taxTreatment: 'TaxTreatment', posSessionStatus: 'CashSessionStatus', cashMovementKind: 'CashMovementKind',
  invoiceKind: 'InvoiceKind', 'inventory.entry.productAction': 'EntryProductAction', 'inventory.entry.offerAction': 'EntryOfferAction',
  'pos.reasons': 'CashMovementReason',
};
// Enum values the UI translates through a composed key (cash reason "Other" → pos.reasons.OtherDeposit/OtherWithdrawal).
const ENUM_IGNORE = new Set(['pos.reasons.Other']);
function backendCoverage() {
  if (!existsSync(backendDir)) return null;
  const cs = walkFiles(backendDir, /\.cs$/).map((f) => ({ f: f.replace(/\\/g, '/'), s: readFileSync(f, 'utf8') }));
  const ui = walkFiles(appDir, /\.(ts|html)$/).filter((f) => !f.endsWith('.spec.ts')).map((f) => readFileSync(f, 'utf8')).join('\n');
  const codes = new Set(['validation.failed']);
  for (const { s } of cs) for (const m of s.matchAll(/Error\.(?:Validation|Forbidden|Conflict|NotFound|Unauthorized)\(\s*"([^"]+)"/g)) codes.add(m[1]);
  const untranslated = [...codes]
    .filter((c) => !CODE_IGNORE.some((re) => re.test(c)))
    .filter((c) => !(`errors.codes.${c}` in de) && !ui.includes(`'${c}'`))
    .sort();
  const enums = {};
  for (const { f, s } of cs) {
    for (const m of s.matchAll(/public enum (\w+)\s*(?::\s*\w+\s*)?\{([^}]*)\}/g)) {
      const module = f.match(/HandyNet\.Modules\.(\w+)\//)?.[1] ?? '';
      const members = m[2].replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\/|\[[^\]]*\]/g, '').split(',').map((x) => x.split('=')[0].trim()).filter(Boolean);
      (enums[m[1]] ??= []).push({ module, members });
    }
  }
  const missingEnums = [];
  for (const [prefix, spec] of Object.entries(ENUMS)) {
    const [module, name] = spec.includes(':') ? spec.split(':') : ['', spec];
    const def = (enums[name] ?? []).find((e) => !module || e.module === module);
    if (!def) {
      missingEnums.push(`${prefix}: backend enum ${spec} not found`);
      continue;
    }
    for (const v of def.members) if (!(`${prefix}.${v}` in de) && !ENUM_IGNORE.has(`${prefix}.${v}`)) missingEnums.push(`${prefix}.${v}`);
  }
  return { codes: codes.size, untranslated, missingEnums };
}

// ------------------------------------------------------------------ report
const report = {};
let total = 0;
const history = useGit ? (() => { try { return gitHistory(); } catch (e) { console.warn('git history unavailable:', e.message); return null; } })() : null;

for (const l of locales) {
  const dict = dicts[l];
  const r = { sameAsDe: [], sameAsEn: [], params: [], tokens: [], numbers: [], stale: [], script: [] };
  for (const [key, deText] of Object.entries(de)) {
    const text = dict[key];
    if (text === undefined || !deText.trim()) continue;
    const allowed = allowedKey(l, key);
    if (!allowed && text === deText && !exempt(text)) r.sameAsDe.push(key);
    else if (!allowed && NON_EN.includes(l) && text === en[key] && en[key] !== deText && !exempt(text)) r.sameAsEn.push(key);
    if (params(text) !== params(deText)) r.params.push(`${key}: de {${params(deText)}} vs {${params(text)}}`);
    if (/§\s?25a/.test(deText) && !/§\s?25a/.test(text)) r.tokens.push(`${key}: "§25a" missing`);
    if (keysOf(deText) && keysOf(deText) !== keysOf(text)) r.tokens.push(`${key}: keys de [${keysOf(deText)}] vs [${keysOf(text)}]`);
    const nDe = numbersOf(deText);
    const nTr = numbersOf(nativeDigits(text));
    if (nDe && nDe !== nTr && !allowed) r.numbers.push(`${key}: de [${nDe}] vs [${nTr}]`);
    if ((l === 'ar' || l === 'fa') && !/[؀-ۿ]/.test(text) && !exempt(text) && !allowed) r.script.push(key);
    if (l === 'ru' && !/[Ѐ-ӿ]/.test(text) && !exempt(text) && !allowed) r.script.push(key);
    if (history && text !== deText) {
      const d = history.lastChange('de', key);
      const t = history.lastChange(l, key);
      if (d > t && t >= 0) {
        const before = history.snaps[d - 1]?.de?.[key];
        r.stale.push(`${key}: de "${before ?? '∅'}" → "${deText}" (${history.snaps[d].sha.slice(0, 7)}), ${l} unchanged since ${history.snaps[t].sha.slice(0, 7)}: "${text}"`);
      }
    }
  }
  report[l] = r;
  total += r.sameAsDe.length + r.sameAsEn.length + r.params.length + r.tokens.length + r.stale.length + r.script.length;
}

const coverage = only ? null : backendCoverage();
if (coverage) total += coverage.untranslated.length + coverage.missingEnums.length;

if (asJson) {
  console.log(JSON.stringify({ locales: report, backend: coverage }, null, 2));
} else {
  if (coverage) {
    console.log(`[backend] ${coverage.codes} API error codes; without errors.codes.* translation or UI mapping: ${coverage.untranslated.length}`);
    for (const c of coverage.untranslated) console.log(`    ${c}`);
    console.log(`[backend] enum values without translation: ${coverage.missingEnums.length}`);
    for (const c of coverage.missingEnums) console.log(`    ${c}`);
  }
  for (const [l, r] of Object.entries(report)) {
    const line = (name, list, max = 400) => {
      if (!list.length) return;
      console.log(`  ${name} (${list.length}):`);
      for (const x of list.slice(0, max)) console.log(`    ${typeof x === 'string' && x in de ? `${x} = "${dicts[l][x]}"` : x}`);
    };
    console.log(`\n[${l}]`);
    line('identical to de', r.sameAsDe);
    line('identical to en', r.sameAsEn);
    line('placeholder mismatch', r.params);
    line('token mismatch', r.tokens);
    line('stale (de changed after translation)', r.stale);
    line('wrong script', r.script);
    line('numbers differ (warning)', r.numbers);
  }
  console.log(
    '\nSummary: ' +
      Object.entries(report)
        .map(([l, r]) => `${l}: ${r.sameAsDe.length} =de, ${r.sameAsEn.length} =en, ${r.params.length} params, ${r.tokens.length} tokens, ${r.stale.length} stale, ${r.script.length} script, ${r.numbers.length} numbers(warn)`)
        .join(' | '),
  );
}
process.exit(total > 0 && !warnOnly ? 1 : 0);
