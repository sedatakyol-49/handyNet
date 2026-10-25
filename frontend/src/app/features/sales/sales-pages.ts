import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { PosSubnavComponent } from '../pos/pos-subnav';
import { SalesDetailComponent } from './sales-detail';
import { SalesFormComponent } from './sales-form';
import { SalesListComponent } from './sales-list';
import { OFFICE_SALES_NAV, posSalesNav } from './sales-nav';

/**
 * Route pages of quotations (Angebote) and invoices (Rechnungen). POS: `/kasse/:registerId/angebote|rechnungen…` with the
 * register tabs (Petrol look, collect at the till); back office: `/app/verkauf/…`. Route data `kind` selects the document.
 */

@Component({
  selector: 'hn-pos-sales-list-page',
  imports: [PosSubnavComponent, SalesListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pos-page block' },
  template: `<hn-pos-subnav [registerId]="registerId()" /><hn-sales-list [kind]="kind()" [nav]="nav()" />`,
})
export class PosSalesListPage {
  readonly registerId = input.required<string>();
  readonly kind = input.required<'quotations' | 'invoices'>();
  protected readonly nav = computed(() => posSalesNav(this.registerId()));
}

@Component({
  selector: 'hn-pos-sales-form-page',
  imports: [PosSubnavComponent, SalesFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pos-page block' },
  template: `<hn-pos-subnav [registerId]="registerId()" /><hn-sales-form [kind]="doc()" [nav]="nav()" [id]="id() ?? null" />`,
})
export class PosSalesFormPage {
  readonly registerId = input.required<string>();
  readonly doc = input.required<'quotation' | 'invoice'>();
  readonly id = input<string | undefined>();
  protected readonly nav = computed(() => posSalesNav(this.registerId()));
}

@Component({
  selector: 'hn-pos-sales-detail-page',
  imports: [PosSubnavComponent, SalesDetailComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'pos-page block' },
  template: `<hn-pos-subnav [registerId]="registerId()" /><hn-sales-detail [kind]="doc()" [nav]="nav()" [id]="id()" />`,
})
export class PosSalesDetailPage {
  readonly registerId = input.required<string>();
  readonly doc = input.required<'quotation' | 'invoice'>();
  readonly id = input.required<string>();
  protected readonly nav = computed(() => posSalesNav(this.registerId()));
}

@Component({
  selector: 'hn-sales-list-page',
  imports: [SalesListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<hn-sales-list [kind]="kind()" [nav]="nav" [overdueOnly]="ueberfaellig() === '1'" />`,
})
export class SalesListPage {
  readonly kind = input.required<'quotations' | 'invoices'>();
  /** `?ueberfaellig=1` (navigation badge) opens the overdue list. */
  readonly ueberfaellig = input<string | undefined>();
  protected readonly nav = OFFICE_SALES_NAV;
}

@Component({
  selector: 'hn-sales-form-page',
  imports: [SalesFormComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<hn-sales-form [kind]="doc()" [nav]="nav" [id]="id() ?? null" />`,
})
export class SalesFormPage {
  readonly doc = input.required<'quotation' | 'invoice'>();
  readonly id = input<string | undefined>();
  protected readonly nav = OFFICE_SALES_NAV;
}

@Component({
  selector: 'hn-sales-detail-page',
  imports: [SalesDetailComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<hn-sales-detail [kind]="doc()" [nav]="nav" [id]="id()" />`,
})
export class SalesDetailPage {
  readonly doc = input.required<'quotation' | 'invoice'>();
  readonly id = input.required<string>();
  protected readonly nav = OFFICE_SALES_NAV;
}
