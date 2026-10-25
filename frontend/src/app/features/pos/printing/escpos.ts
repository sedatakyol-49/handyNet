/**
 * Minimal ESC/POS encoder (pure, no DOM). Produces the byte stream that WebUSB, WebSerial and the SEHA Print Bridge send
 * to a thermal printer (ADR-0018). Commands follow the Epson ESC/POS reference, which most 58/80 mm printers implement.
 */

export type Codepage = 'Cp858' | 'Pc437';
export type Align = 'left' | 'center' | 'right';

/** Upper halves (0x80–0xFF) of the code pages. CP858 = CP850 with € at 0xD5. */
const CP850_HIGH =
  'ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜø£Ø×ƒáíóúñÑªº¿®¬½¼¡«»░▒▓│┤ÁÂÀ©╣║╗╝¢¥┐└┴┬├─┼ãÃ╚╔╩╦╠═╬¤ðÐÊËÈıÍÎÏ┘┌█▄¦Ì▀ÓßÔÒõÕµþÞÚÛÙýÝ¯´­±‗¾¶§÷¸°¨·¹³²■ ';
const CP437_HIGH =
  'ÇüéâäàåçêëèïîìÄÅÉæÆôöòûùÿÖÜ¢£¥₧ƒáíóúñÑªº¿⌐¬½¼¡«»░▒▓│┤╡╢╖╕╣║╗╝╜╛┐└┴┬├─┼╞╟╚╔╩╦╠═╬╧╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀αßΓπΣσµτΦΘΩδ∞φε∩≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ';

function table(high: string, overrides: Record<string, number> = {}): Map<string, number> {
  const chars = [...high];
  if (chars.length !== 128) throw new Error('code page table must have 128 entries');
  const map = new Map<string, number>();
  chars.forEach((c, i) => map.set(c, 0x80 + i));
  for (const [c, b] of Object.entries(overrides)) map.set(c, b);
  return map;
}

const TABLES: Record<Codepage, Map<string, number>> = {
  Cp858: (() => {
    const t = table(CP850_HIGH);
    t.delete('ı');
    t.set('€', 0xd5);
    return t;
  })(),
  Pc437: table(CP437_HIGH),
};

/** ESC t n: character code table number (Epson numbering: 0 = PC437, 19 = PC858). */
export const CODEPAGE_NUMBER: Record<Codepage, number> = { Cp858: 19, Pc437: 0 };

/** Replacements for characters a code page lacks (applied before the ? fallback). */
const TRANSLIT: Record<string, string> = {
  '€': 'EUR', '§': 'Par.', '–': '-', '—': '-', '‑': '-', '−': '-', '…': '...', '„': '"', '“': '"', '”': '"', '‚': "'", '‘': "'", '’': "'",
  '×': 'x', '•': '*', '·': '.', ' ': ' ', ' ': ' ', ' ': ' ', '‎': '', '‏': '', '©': '(c)', '®': '(R)',
  '\t': ' ',
};

/**
 * The text as the printer can print it in the code page: characters it lacks are transliterated (ş → s, € → EUR and
 * § → Par. on PC437) or become "?"; control characters other than line feeds are dropped. Layout works on this string
 * so that column widths match the paper.
 */
export function toPrintable(text: string, codepage: Codepage): string {
  const map = TABLES[codepage];
  const one = (c: string, depth: number): string => {
    const code = c.codePointAt(0)!;
    if (code === 0x0a || (code >= 0x20 && code < 0x7f) || map.has(c)) return c;
    if (depth > 0) return '?';
    const translit = TRANSLIT[c];
    if (translit !== undefined) return [...translit].map((t) => one(t, 1)).join('');
    if (code < 0x20 || code === 0x7f) return '';
    const base = c.normalize('NFD').replace(/\p{M}/gu, '');
    if (base && base !== c) return [...base].map((t) => one(t, 1)).join('');
    return '?';
  };
  let out = '';
  // Intl formats money and dates with (narrow) no-break spaces; the printer gets plain spaces.
  for (const c of text.replace(/[    ]/g, ' ')) out += one(c, 0);
  return out;
}

/** Encodes text in the code page (see toPrintable for characters it lacks). */
export function encodeText(text: string, codepage: Codepage): number[] {
  const map = TABLES[codepage];
  return [...toPrintable(text, codepage)].map((c) => (c.charCodeAt(0) < 0x80 ? c.charCodeAt(0) : (map.get(c) ?? 0x3f)));
}

const ESC = 0x1b;
const GS = 0x1d;

/** Builder for an ESC/POS byte stream. */
export class EscPos {
  private readonly bytes: number[] = [];

  constructor(private readonly codepage: Codepage = 'Cp858') {}

  /** ESC @ (reset) and ESC t (code page). */
  init(): this {
    return this.raw(ESC, 0x40, ESC, 0x74, CODEPAGE_NUMBER[this.codepage]);
  }

  align(a: Align): this {
    return this.raw(ESC, 0x61, a === 'left' ? 0 : a === 'center' ? 1 : 2);
  }

  bold(on: boolean): this {
    return this.raw(ESC, 0x45, on ? 1 : 0);
  }

  /** GS ! n: character size (width and height multipliers 1–2). */
  size(width: 1 | 2, height: 1 | 2): this {
    return this.raw(GS, 0x21, ((width - 1) << 4) | (height - 1));
  }

  text(text: string): this {
    this.bytes.push(...encodeText(text, this.codepage));
    return this;
  }

  line(text = ''): this {
    return this.text(text).raw(0x0a);
  }

  /** ESC d n: print and feed n lines. */
  feed(lines = 1): this {
    return this.raw(ESC, 0x64, clamp(lines, 0, 255));
  }

  /** GS V 66 n: feed n lines and partial cut. */
  cut(feedLines = 3): this {
    return this.raw(GS, 0x56, 0x42, clamp(feedLines, 0, 255));
  }

  /** ESC p m t1 t2: drawer kick on pin 2 (connector pin "drawer 1"), 50 ms on / 500 ms off. */
  drawer(): this {
    return this.raw(ESC, 0x70, 0x00, 0x19, 0xfa);
  }

  /**
   * QR code via GS ( k (model 2, error correction M). Data must be ASCII (the DSFinV-K payload is); returns false and
   * prints nothing when the data is empty, not ASCII or longer than the printer's 7089-byte store.
   */
  qr(data: string, moduleSize = 4): boolean {
    if (!data || data.length > 7089 || !/^[\x20-\x7e]+$/.test(data)) return false;
    const payload = [...data].map((c) => c.charCodeAt(0));
    const len = payload.length + 3;
    this.raw(GS, 0x28, 0x6b, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00); // model 2
    this.raw(GS, 0x28, 0x6b, 0x03, 0x00, 0x31, 0x43, clamp(moduleSize, 1, 16)); // module size
    this.raw(GS, 0x28, 0x6b, 0x03, 0x00, 0x31, 0x45, 0x31); // error correction M
    this.raw(GS, 0x28, 0x6b, len & 0xff, (len >> 8) & 0xff, 0x31, 0x50, 0x30, ...payload); // store
    this.raw(GS, 0x28, 0x6b, 0x03, 0x00, 0x31, 0x51, 0x30); // print
    return true;
  }

  raw(...bytes: number[]): this {
    for (const b of bytes) this.bytes.push(b & 0xff);
    return this;
  }

  build(): Uint8Array {
    return Uint8Array.from(this.bytes);
  }
}

function clamp(n: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, Math.round(n)));
}

/** Word wrap to `width` characters; words longer than a line are broken hard. Empty text gives one empty line. */
export function wrap(text: string, width: number): string[] {
  const w = Math.max(1, width);
  const out: string[] = [];
  for (const paragraph of text.replace(/\r\n?/g, '\n').split('\n')) {
    let line = '';
    for (const word of paragraph.split(/\s+/).filter(Boolean)) {
      let rest = word;
      while (rest.length > w) {
        if (line) {
          out.push(line);
          line = '';
        }
        out.push(rest.slice(0, w));
        rest = rest.slice(w);
      }
      if (!rest) continue;
      if (!line) line = rest;
      else if (line.length + 1 + rest.length <= w) line += ' ' + rest;
      else {
        out.push(line);
        line = rest;
      }
    }
    out.push(line);
  }
  return out;
}

/**
 * Two columns on one line: the left text wraps, the right text (an amount) is right-aligned on the last line. If the
 * right text does not fit next to the left text's last line, it gets its own right-aligned line.
 */
export function columns(left: string, right: string, width: number): string[] {
  const r = right.trim();
  if (!r) return wrap(left, width);
  if (r.length >= width) return [...wrap(left, width), r.slice(0, width)];
  const lines = wrap(left, width);
  const last = lines[lines.length - 1];
  if (last.length + 1 + r.length <= width) {
    lines[lines.length - 1] = last + ' '.repeat(width - last.length - r.length) + r;
    return lines;
  }
  return [...lines, ' '.repeat(width - r.length) + r];
}
