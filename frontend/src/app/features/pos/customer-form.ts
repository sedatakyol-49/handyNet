import { PosCustomer, PosCustomerInput } from '../../core/api/models';

/** Limits of the API (`Customer.Max*Length`). */
export const CUSTOMER_LIMITS = {
  name: 200,
  company: 200,
  email: 320,
  phone: 50,
  street: 200,
  postalCode: 10,
  city: 100,
  vatId: 20,
} as const;

export interface CustomerForm {
  name: string;
  company: string;
  email: string;
  phone: string;
  street: string;
  postalCode: string;
  city: string;
  countryCode: string;
  vatId: string;
}

export type CustomerField = keyof CustomerForm;
export type CustomerFormErrors = Partial<Record<CustomerField, 'required' | 'tooLong' | 'invalid'>>;

export function emptyCustomerForm(): CustomerForm {
  return { name: '', company: '', email: '', phone: '', street: '', postalCode: '', city: '', countryCode: 'DE', vatId: '' };
}

export function customerToForm(c: PosCustomer): CustomerForm {
  return {
    name: c.name,
    company: c.company ?? '',
    email: c.email ?? '',
    phone: c.phone ?? '',
    street: c.street ?? '',
    postalCode: c.postalCode ?? '',
    city: c.city ?? '',
    countryCode: c.countryCode ?? '',
    vatId: c.vatId ?? '',
  };
}

/** Client checks mirroring the API (name required; email, 2-letter country, VAT id 4–20 letters/digits). */
export function validateCustomer(f: CustomerForm): CustomerFormErrors {
  const e: CustomerFormErrors = {};
  const t = (s: string) => s.trim();
  if (!t(f.name)) e.name = 'required';
  for (const k of ['name', 'company', 'email', 'phone', 'street', 'postalCode', 'city'] as const) {
    if (!e[k] && t(f[k]).length > CUSTOMER_LIMITS[k]) e[k] = 'tooLong';
  }
  if (!e.email && t(f.email) && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(t(f.email))) e.email = 'invalid';
  if (t(f.countryCode) && !/^[A-Za-z]{2}$/.test(t(f.countryCode))) e.countryCode = 'invalid';
  const vat = t(f.vatId).replace(/\s/g, '');
  if (vat && !/^[A-Za-z0-9]{4,20}$/.test(vat)) e.vatId = 'invalid';
  return e;
}

/** Form → request body (empty optional fields become null; country upper-case; VAT id without spaces). */
export function customerRequest(f: CustomerForm): PosCustomerInput {
  const opt = (s: string) => (s.trim() ? s.trim() : null);
  return {
    name: f.name.trim(),
    company: opt(f.company),
    email: opt(f.email),
    phone: opt(f.phone),
    street: opt(f.street),
    postalCode: opt(f.postalCode),
    city: opt(f.city),
    countryCode: opt(f.countryCode)?.toUpperCase() ?? null,
    vatId: opt(f.vatId.replace(/\s/g, ''))?.toUpperCase() ?? null,
  };
}

/** One-line summary for lists and the ticket ("Firma · PLZ Ort"). */
export function customerSummary(c: Pick<PosCustomer, 'company' | 'postalCode' | 'city'>): string {
  const place = [c.postalCode, c.city].filter((s) => !!s && s.trim()).join(' ');
  return [c.company, place].filter((s) => !!s && s.trim()).join(' · ');
}
