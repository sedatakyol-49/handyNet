/**
 * Ankauf runs in the back office (`/app/ankauf/…`) and inside the POS app (`/kasse/:registerId/ankauf/…`, Petrol design,
 * the register preselected for cash payouts). The pages derive all their links from the current URL.
 */
export interface AnkaufLinks {
  /** Register of the POS context (null in the back office). */
  registerId: string | null;
  quotes: string;
  intakes: string;
  intake: (id: string) => string[];
  newIntake: (quoteId: string) => string[];
  /** Stock (Lager) is a back-office page in both contexts. */
  stock: string;
}

export function ankaufLinks(url: string): AnkaufLinks {
  const pos = /^\/kasse\/([^/?#]+)\/ankauf(?:[/?#]|$)/.exec(url ?? '');
  const base = pos ? `/kasse/${pos[1]}/ankauf` : '/app/ankauf';
  return {
    registerId: pos ? decodeURIComponent(pos[1]) : null,
    quotes: base,
    intakes: `${base}/ankaeufe`,
    intake: (id) => [`${base}/ankaeufe`, id],
    newIntake: (quoteId) => [`${base}/ankaeufe/neu`, quoteId],
    stock: '/app/lager',
  };
}
