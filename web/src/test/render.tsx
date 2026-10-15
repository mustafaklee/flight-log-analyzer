import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router';
import { vi } from 'vitest';
import { createQueryClient, Providers } from '../App';

/** Renders inside the app's providers with a fresh query cache and a router at the given address. */
export function renderWithApp(ui: ReactElement, path = '/') {
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { ...client.getDefaultOptions().queries, retry: false } });
  return render(
    <Providers client={client}>
      <MemoryRouter initialEntries={[path]}>{ui}</MemoryRouter>
    </Providers>,
  );
}

type Handler = (url: URL, init: RequestInit | undefined) => { status?: number; body?: unknown } | undefined;

/** Replaces fetch with a handler keyed on the request; unknown requests fail the test loudly with 599. */
export function mockApi(handler: Handler) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    const result = handler(url, init) ?? { status: 599, body: { title: `unexpected ${url.pathname}` } };
    const status = result.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(result.body ?? null), { status, headers: { 'content-type': 'application/json' } });
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}
