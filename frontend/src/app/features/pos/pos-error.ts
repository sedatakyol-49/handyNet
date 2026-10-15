import { toApiProblem } from '../../core/api/api-problem';
import { I18nService } from '../../core/i18n/i18n.service';
import { describeProblem } from '../../core/ui-state/toast.service';
import { posProblemMessage } from './pos.logic';

/**
 * User text for a POS API error: data-dependent messages (e.g. available stock), otherwise the translated
 * `errors.codes.<code>` text or the API title. Field validation errors (422) append the first field message.
 */
export function posErrorText(i18n: I18nService, error: unknown): string | null {
  if (!error) return null;
  const problem = toApiProblem(error);
  const special = posProblemMessage(problem.code, error);
  if (special) return i18n.t(special.key, special.params);
  const text = describeProblem(i18n, problem);
  const fields = Object.values(problem.errors).flat();
  return fields.length && !(problem.code && i18n.has(`errors.codes.${problem.code}`)) ? `${text} ${fields[0]}` : text;
}
