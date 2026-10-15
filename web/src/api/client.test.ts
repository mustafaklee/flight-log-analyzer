import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockApi } from '../test/render';
import { api, ApiError, fileNameFrom, session, toQueryString } from './client';

afterEach(() => {
  vi.unstubAllGlobals();
  session.clear();
});

const tokens = (access: string) => ({ tokenType: 'Bearer', accessToken: access, expiresIn: 3600, refreshToken: `refresh-${access}` });

describe('toQueryString', () => {
  it('leaves out empty values and joins arrays', () => {
    expect(toQueryString({ a: 1, b: '', c: null, d: undefined, names: ['x.y', 'z'], flag: false })).toBe('?a=1&names=x.y%2Cz&flag=false');
    expect(toQueryString({})).toBe('');
  });
});

describe('api', () => {
  it('renews the access token once on 401 and repeats the request', async () => {
    session.store(tokens('old'));
    const fetchMock = mockApi((url, init) => {
      const auth = (init?.headers as Record<string, string> | undefined)?.authorization;
      if (url.pathname === '/api/v1/auth/refresh') {
        return { body: tokens('new') };
      }

      if (url.pathname === '/api/v1/teams') {
        return auth === 'Bearer new' ? { body: [{ id: 't1' }] } : { status: 401, body: {} };
      }

      return undefined;
    });

    await expect(api('/teams')).resolves.toEqual([{ id: 't1' }]);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(localStorage.getItem('flightlog.refreshToken')).toBe('refresh-new');
  });

  it('signs out when the refresh token is rejected', async () => {
    session.store(tokens('old'));
    mockApi(() => ({ status: 401, body: { title: 'Unauthorized' } }));

    await expect(api('/teams')).rejects.toBeInstanceOf(ApiError);
    expect(session.hasRefreshToken).toBe(false);
  });

  it('exposes problem details and field errors', async () => {
    mockApi(() => ({ status: 400, body: { title: 'Validation', code: 'validation', errors: { name: ['Ad zorunlu.'] } } }));

    const error = await api('/teams', { method: 'POST', body: {}, anonymous: true }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).problem.code).toBe('validation');
    expect((error as ApiError).fieldErrors).toEqual(['Ad zorunlu.']);
  });
});

describe('fileNameFrom', () => {
  it('prefers the UTF-8 file name of Content-Disposition', () => {
    expect(fileNameFrom(`attachment; filename=ucus-bulgular.csv; filename*=UTF-8''u%C3%A7u%C5%9F-bulgular.csv`)).toBe('uçuş-bulgular.csv');
    expect(fileNameFrom('attachment; filename="rapor.json"')).toBe('rapor.json');
    expect(fileNameFrom(null)).toBeNull();
  });
});
