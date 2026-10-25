import { qrSvgPath } from '../fiscal.logic';
import { EscPos } from './escpos';
import { PrintItem, PrintLayout } from './receipt-layout';

/** Output options shared by all printer languages. */
export interface RenderOptions {
  copies: number;
  cut: boolean;
  /** Kick the cash drawer once, before the first copy. */
  drawer: boolean;
  paperWidthMm: number;
}

/** QR module size in dots: 80 mm heads have 576 dots, 58 mm heads 384. */
export function qrModuleSize(paperWidthMm: number): number {
  return paperWidthMm >= 80 ? 4 : 3;
}

function copies(n: number): number {
  return Math.max(1, Math.min(5, Math.floor(n || 1)));
}

// ------------------------------------------------------------------------------------------------ ESC/POS (raw bytes)

export function toEscPos(layout: PrintLayout | null, o: RenderOptions): Uint8Array {
  const e = new EscPos(layout?.codepage ?? 'Cp858').init();
  if (o.drawer) e.drawer();
  if (!layout) return e.build();
  for (let c = 0; c < copies(o.copies); c++) {
    for (const item of layout.items) escPosItem(e, item, layout.width, o.paperWidthMm);
    if (o.cut) e.cut(4);
    else e.feed(4);
  }
  return e.build();
}

function escPosItem(e: EscPos, item: PrintItem, width: number, paper: number): void {
  switch (item.kind) {
    case 'text':
      e.align(item.align).bold(!!item.bold).size(item.size === 2 ? 2 : 1, item.size === 2 ? 2 : 1).line(item.text);
      e.bold(false).size(1, 1).align('left');
      return;
    case 'rule':
      e.line('-'.repeat(width));
      return;
    case 'feed':
      e.feed(item.lines);
      return;
    case 'qr':
      e.align('center');
      if (e.qr(item.data, qrModuleSize(paper))) e.line();
      e.align('left');
      return;
  }
}

// ------------------------------------------------------------------------------------------------ Epson ePOS-Print XML

export function xmlEscape(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&apos;');
}

/** SOAP envelope for POST {printer}/cgi-bin/epos/service.cgi (Epson ePOS-Print XML). */
export function toEposXml(layout: PrintLayout | null, o: RenderOptions): string {
  let body = '';
  if (o.drawer) body += '<pulse drawer="drawer_1" time="pulse_100"/>';
  if (layout) {
    for (let c = 0; c < copies(o.copies); c++) {
      for (const item of layout.items) body += eposItem(item, layout.width, o.paperWidthMm);
      body += o.cut ? '<feed line="3"/><cut type="feed"/>' : '<feed line="4"/>';
    }
  }
  return (
    '<?xml version="1.0" encoding="utf-8"?>' +
    '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>' +
    `<epos-print xmlns="http://www.epson-pos.com/schemas/2011/03/epos-print">${body}</epos-print>` +
    '</s:Body></s:Envelope>'
  );
}

function eposItem(item: PrintItem, width: number, paper: number): string {
  switch (item.kind) {
    case 'text': {
      const big = item.size === 2 ? 'true' : 'false';
      return `<text align="${item.align}" em="${item.bold ? 'true' : 'false'}" dw="${big}" dh="${big}">${xmlEscape(item.text)}&#10;</text>`;
    }
    case 'rule':
      return `<text align="left" em="false" dw="false" dh="false">${'-'.repeat(width)}&#10;</text>`;
    case 'feed':
      return `<feed line="${item.lines}"/>`;
    case 'qr':
      return `<text align="center"/><symbol type="qrcode_model_2" level="level_m" width="${qrModuleSize(paper) + 1}">${xmlEscape(item.data)}</symbol><feed line="1"/><text align="left"/>`;
  }
}

/** Parses the ePOS-Print response: success flag and the printer's error code (e.g. EPTR_COVER_OPEN). */
export function parseEposResponse(xml: string): { ok: boolean; code: string | null } {
  const tag = /<response\b[^>]*>/i.exec(xml)?.[0] ?? '';
  const ok = /\bsuccess\s*=\s*"true"/i.test(tag);
  const code = /\bcode\s*=\s*"([^"]*)"/i.exec(tag)?.[1] || null;
  return { ok, code };
}

// ------------------------------------------------------------------------------------------------ Star WebPRNT XML

/** Star builder escaping: backslash and control characters as \xNN, then XML entities. */
function starText(text: string): string {
  const escaped = text.replace(/[\\\x00-\x1f]/g, (c) => (c === '\\' ? '\\\\' : `\\x${c.charCodeAt(0).toString(16).padStart(2, '0')}`));
  return xmlEscape(escaped);
}

/** Print request for Star WebPRNT (StarWebPrintBuilder elements inside a root element). */
export function toStarRequest(layout: PrintLayout | null, o: RenderOptions): string {
  const cp = layout?.codepage === 'Pc437' ? 'cp437' : 'cp858';
  let r = '<initialization/>';
  if (o.drawer) r += '<peripheral channel="1" on="200" off="200"/>';
  if (layout) {
    for (let c = 0; c < copies(o.copies); c++) {
      for (const item of layout.items) {
        switch (item.kind) {
          case 'text': {
            const s = item.size === 2 ? 2 : 1;
            r += `<alignment position="${item.align}"/><text codepage="${cp}" international="germany" emphasis="${item.bold ? 'true' : 'false'}" width="${s}" height="${s}">${starText(item.text + '\n')}</text>`;
            break;
          }
          case 'rule':
            r += `<alignment position="left"/><text emphasis="false" width="1" height="1">${starText('-'.repeat(layout.width) + '\n')}</text>`;
            break;
          case 'feed':
            r += `<feed line="${item.lines}"/>`;
            break;
          case 'qr':
            r += `<alignment position="center"/><qrcode model="model2" level="level_m" cell="${qrModuleSize(o.paperWidthMm) + 1}">${starText(item.data)}</qrcode><feed line="1"/>`;
            break;
        }
      }
      r += o.cut ? '<cutpaper feed="true" type="partial"/>' : '<feed line="4"/>';
    }
  }
  return `<root>${r}</root>`;
}

/** SOAP-like envelope for POST {printer}/StarWebPRNT/SendMessage. */
export function toStarEnvelope(request: string): string {
  return (
    '<StarWebPrint xmlns="http://www.star-m.jp" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">' +
    `<Request>${xmlEscape(request)}</Request></StarWebPrint>`
  );
}

export function parseStarResponse(xml: string): { ok: boolean; code: string | null } {
  const ok = /<success>\s*true\s*<\/success>/i.test(xml);
  const code = /<code>([^<]*)<\/code>/i.exec(xml)?.[1]?.trim() || null;
  return { ok, code };
}

// ------------------------------------------------------------------------------------------------ HTML (browser print)

/** A standalone HTML page of the layout, sized to the paper width (for the hidden print iframe). */
export function toHtml(layout: PrintLayout, o: Pick<RenderOptions, 'paperWidthMm' | 'copies'>, title: string): string {
  const content = o.paperWidthMm >= 80 ? 72 : 48;
  const fontMm = Math.min(3.6, content / (layout.width * 0.6));
  const one = layout.items
    .map((item) => {
      switch (item.kind) {
        case 'text': {
          const style = `text-align:${item.align};${item.bold ? 'font-weight:700;' : ''}${item.size === 2 ? 'font-size:2em;line-height:1.1;' : ''}`;
          return `<div style="${style}">${xmlEscape(item.text) || '&nbsp;'}</div>`;
        }
        case 'rule':
          return '<div class="rule"></div>';
        case 'feed':
          return '<div>&nbsp;</div>'.repeat(Math.min(item.lines, 5));
        case 'qr': {
          try {
            const { size, path } = qrSvgPath(item.data);
            return `<div style="text-align:center"><svg viewBox="-2 -2 ${size + 4} ${size + 4}" style="width:30mm;height:30mm" shape-rendering="crispEdges"><path d="${path}" fill="#000"/></svg></div>`;
          } catch {
            return '';
          }
        }
      }
    })
    .join('');
  const pages = Array.from({ length: copies(o.copies) }, () => `<section>${one}</section>`).join('');
  return (
    `<!doctype html><html><head><meta charset="utf-8"><title>${xmlEscape(title)}</title><style>` +
    `@page{size:${o.paperWidthMm}mm auto;margin:3mm}html,body{margin:0;background:#fff;color:#000}` +
    `body{width:${content}mm;font-family:"Courier New",ui-monospace,monospace;font-size:${fontMm.toFixed(2)}mm;line-height:1.25;white-space:pre}` +
    `.rule{border-top:1px dashed #000;margin:1mm 0}section+section{break-before:page}` +
    `</style></head><body>${pages}</body></html>`
  );
}
