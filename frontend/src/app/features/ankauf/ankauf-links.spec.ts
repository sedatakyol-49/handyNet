import { ankaufLinks } from './ankauf-links';

describe('ankaufLinks', () => {
  it('stays in the back office', () => {
    const l = ankaufLinks('/app/ankauf/ankaeufe/neu/q1');
    expect(l.registerId).toBeNull();
    expect(l.intakes).toBe('/app/ankauf/ankaeufe');
    expect(l.intake('i1')).toEqual(['/app/ankauf/ankaeufe', 'i1']);
  });

  it('stays in the POS app with the register', () => {
    const l = ankaufLinks('/kasse/r-1/ankauf/ankaeufe?x=1');
    expect(l.registerId).toBe('r-1');
    expect(l.quotes).toBe('/kasse/r-1/ankauf');
    expect(l.newIntake('q')).toEqual(['/kasse/r-1/ankauf/ankaeufe/neu', 'q']);
    expect(ankaufLinks('/kasse/r-1/ankauf').registerId).toBe('r-1');
    expect(ankaufLinks('/kasse/r-1/belege').registerId).toBeNull();
  });
});
