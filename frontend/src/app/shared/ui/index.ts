export { ButtonComponent } from './button';
export { FieldComponent, InputDirective, SelectDirective } from './field';
export { StatusBadgeComponent } from './status-badge';
export { PaginationComponent } from './pagination';
export { TableComponent } from './table';
export { PageHeaderComponent, EmptyStateComponent, BannerComponent, ToastHostComponent, DialogComponent, SkeletonRowsComponent } from './misc';
export { ActionsMenuComponent, FilterBarComponent } from './responsive';
export { TranslatePipe, TranslateEnumPipe } from '../../core/i18n/translate.pipe';
export { MoneyPipe, SignedMoneyPipe } from '../../core/format/money';
export { HnDatePipe } from '../../core/format/date.pipe';

import { ButtonComponent } from './button';
import { FieldComponent, InputDirective, SelectDirective } from './field';
import { StatusBadgeComponent } from './status-badge';
import { PaginationComponent } from './pagination';
import { TableComponent } from './table';
import { ActionsMenuComponent, FilterBarComponent } from './responsive';
import { PageHeaderComponent, EmptyStateComponent, BannerComponent, DialogComponent, SkeletonRowsComponent } from './misc';
import { TranslatePipe, TranslateEnumPipe } from '../../core/i18n/translate.pipe';
import { MoneyPipe, SignedMoneyPipe } from '../../core/format/money';
import { HnDatePipe } from '../../core/format/date.pipe';

/** Convenience bundle for feature components: `imports: [...UI]`. */
export const UI = [
  ButtonComponent,
  FieldComponent,
  InputDirective,
  SelectDirective,
  StatusBadgeComponent,
  PaginationComponent,
  TableComponent,
  ActionsMenuComponent,
  FilterBarComponent,
  PageHeaderComponent,
  EmptyStateComponent,
  BannerComponent,
  DialogComponent,
  SkeletonRowsComponent,
  TranslatePipe,
  TranslateEnumPipe,
  MoneyPipe,
  SignedMoneyPipe,
  HnDatePipe,
] as const;
