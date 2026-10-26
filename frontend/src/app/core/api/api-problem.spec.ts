import { HttpErrorResponse } from '@angular/common/http';
import { fieldErrors, setValidationTranslator, toApiProblem } from './api-problem';

describe('toApiProblem', () => {
  it('reads RFC 7807 bodies including validation errors', () => {
    const problem = toApiProblem(
      new HttpErrorResponse({
        status: 422,
        error: {
          title: 'One or more validation errors occurred.',
          status: 422,
          code: 'validation.failed',
          errors: { 'Organization.PostalCode': ['German postal codes have five digits.'], Email: ['Invalid'] },
        },
      }),
    );
    expect(problem.status).toBe(422);
    expect(problem.code).toBe('validation.failed');
    expect(fieldErrors(problem, 'organization.postalCode')).toEqual(['German postal codes have five digits.']);
    expect(fieldErrors(problem, 'email')).toEqual(['Invalid']);
    expect(fieldErrors(problem, 'postalCode')).toEqual(['German postal codes have five digits.']);
    expect(fieldErrors(problem, 'city')).toEqual([]);
  });

  it('translates field errors by code and falls back to the server text', () => {
    setValidationTranslator((code, params) =>
      code === 'validation.maxLength' ? `max ${params?.['maxLength']}` : null,
    );
    try {
      const problem = toApiProblem(
        new HttpErrorResponse({
          status: 422,
          error: {
            title: 'x',
            code: 'validation.failed',
            errors: { Name: ['too long', 'odd'], Other: ['Unknown thing'] },
            errorCodes: {
              Name: [{ code: 'validation.maxLength', params: { maxLength: '5' } }, { code: null }],
              Other: [{ code: 'validation.unknown' }],
            },
          },
        }),
      );
      expect(fieldErrors(problem, 'name')).toEqual(['max 5', 'odd']);
      expect(fieldErrors(problem, 'other')).toEqual(['Unknown thing']);
    } finally {
      setValidationTranslator(null);
    }
  });

  it('marks network failures', () => {
    expect(toApiProblem(new HttpErrorResponse({ status: 0 })).code).toBe('network');
  });
});
