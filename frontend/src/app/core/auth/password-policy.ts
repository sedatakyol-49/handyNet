/** Mirrors the server password policy: min. 10 characters, letters plus a digit or symbol. */
export function meetsPasswordPolicy(value: string): boolean {
  return value.length >= 10 && /\p{L}/u.test(value) && /[^\p{L}]/u.test(value);
}

export interface NewPasswordInput {
  newPassword: string;
  confirmPassword: string;
  /** Only for "change password"; omit for the reset form. */
  currentPassword?: string;
}

/** Client-side checks of the set/change password forms; values are i18n keys per field (empty = valid). */
export function newPasswordErrors(input: NewPasswordInput): Partial<Record<keyof NewPasswordInput, string>> {
  const errors: Partial<Record<keyof NewPasswordInput, string>> = {};
  if (input.currentPassword !== undefined && !input.currentPassword) errors.currentPassword = 'validation.required';
  if (!input.newPassword) errors.newPassword = 'validation.required';
  else if (!meetsPasswordPolicy(input.newPassword)) errors.newPassword = 'validation.passwordPolicy';
  else if (input.currentPassword && input.newPassword === input.currentPassword) errors.newPassword = 'validation.passwordUnchanged';
  if (!input.confirmPassword) errors.confirmPassword = 'validation.required';
  else if (input.confirmPassword !== input.newPassword) errors.confirmPassword = 'validation.passwordMismatch';
  return errors;
}
