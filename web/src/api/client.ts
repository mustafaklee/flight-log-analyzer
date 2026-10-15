import type { AccessTokenResponse, Problem } from './types';

/** An error response of the API, carrying its problem details. */
export class ApiError extends Error {
  readonly status: number;
  readonly problem: Problem;

  constructor(status: number, problem: Problem) {
    super(problem.detail ?? problem.title ?? `HTTP ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }

  /** Field validation messages, flattened. */
  get fieldErrors(): string[] {
    return Object.values(this.problem.errors ?? {}).flat();
  }
}

const REFRESH_KEY = 'flightlog.refreshToken';

type Listener = (signedIn: boolean) => void;

/**
 * Holds the tokens (access token in memory only, refresh token in localStorage; ADR-0010) and performs requests,
 * renewing the access token once when the API answers 401.
 */
class Session {
  private accessToken: string | null = null;
  private expiresAt = 0;
  private refreshing: Promise<boolean> | null = null;
  private listeners = new Set<Listener>();

  get hasRefreshToken(): boolean {
    return readRefreshToken() !== null;
  }

  subscribe(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  store(tokens: AccessTokenResponse): void {
    this.accessToken = tokens.accessToken;
    this.expiresAt = Date.now() + tokens.expiresIn * 1000;
    writeRefreshToken(tokens.refreshToken);
    this.listeners.forEach((l) => l(true));
  }

  clear(): void {
    this.accessToken = null;
    this.expiresAt = 0;
    writeRefreshToken(null);
    this.listeners.forEach((l) => l(false));
  }

  /** A valid access token, renewed when it is about to expire; null when signed out. */
  async getAccessToken(): Promise<string | null> {
    if (this.accessToken && Date.now() < this.expiresAt - 30_000) {
      return this.accessToken;
    }

    return (await this.refresh()) ? this.accessToken : null;
  }

  /** Exchanges the refresh token for a new pair; concurrent callers share one request. */
  refresh(): Promise<boolean> {
    this.refreshing ??= (async () => {
      const refreshToken = readRefreshToken();
      if (!refreshToken) {
        return false;
      }

      try {
        const response = await fetch('/api/v1/auth/refresh', {
          method: 'POST',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify({ refreshToken }),
        });
        if (!response.ok) {
          if (response.status === 401) {
            this.clear();
          }

          return false;
        }

        this.store((await response.json()) as AccessTokenResponse);
        return true;
      } catch {
        return false;
      } finally {
        this.refreshing = null;
      }
    })();
    return this.refreshing;
  }
}

function readRefreshToken(): string | null {
  try {
    return localStorage.getItem(REFRESH_KEY);
  } catch {
    return null;
  }
}

function writeRefreshToken(token: string | null): void {
  try {
    if (token) {
      localStorage.setItem(REFRESH_KEY, token);
    } else {
      localStorage.removeItem(REFRESH_KEY);
    }
  } catch {
    // Storage unavailable (private mode): the session lasts until the tab closes.
  }
}

export const session = new Session();

export type Query = Record<string, string | number | boolean | null | undefined | string[]>;

/** Builds a query string; empty values are left out, arrays become comma-separated lists. */
export function toQueryString(query?: Query): string {
  if (!query) {
    return '';
  }

  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') {
      continue;
    }

    params.set(key, Array.isArray(value) ? value.join(',') : String(value));
  }

  const text = params.toString();
  return text ? `?${text}` : '';
}

interface RequestOptions {
  method?: string;
  query?: Query;
  body?: unknown;
  signal?: AbortSignal;
  anonymous?: boolean;
}

export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const send = async (token: string | null) => {
    const headers: Record<string, string> = { accept: 'application/json' };
    if (options.body !== undefined) {
      headers['content-type'] = 'application/json';
    }

    if (token) {
      headers.authorization = `Bearer ${token}`;
    }

    return fetch(`/api/v1${path}${toQueryString(options.query)}`, {
      method: options.method ?? 'GET',
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    });
  };

  let response = await send(options.anonymous ? null : await session.getAccessToken());
  if (response.status === 401 && !options.anonymous && (await session.refresh())) {
    response = await send(await session.getAccessToken());
  }

  if (response.status === 401 && !options.anonymous) {
    session.clear();
  }

  return readResponse<T>(response);
}

export async function readResponse<T>(response: Response): Promise<T> {
  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  const body: unknown = text ? safeParse(text) : undefined;
  if (!response.ok) {
    const problem: Problem = typeof body === 'object' && body !== null ? (body as Problem) : { title: response.statusText, detail: text };
    throw new ApiError(response.status, problem);
  }

  return body as T;
}

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/**
 * Uploads a file with progress (fetch has no upload progress). Resolves with the parsed body; rejects with ApiError.
 */
export async function upload<T>(path: string, file: File, query: Query, onProgress: (fraction: number) => void, signal?: AbortSignal): Promise<T> {
  const token = await session.getAccessToken();
  return new Promise<T>((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', `/api/v1${path}${toQueryString(query)}`);
    if (token) {
      xhr.setRequestHeader('authorization', `Bearer ${token}`);
    }

    xhr.setRequestHeader('accept', 'application/json');
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) {
        onProgress(e.loaded / e.total);
      }
    };
    xhr.onload = () => {
      const response = new Response(xhr.status === 204 ? null : xhr.responseText, { status: xhr.status, statusText: xhr.statusText });
      readResponse<T>(response).then(resolve, reject);
    };
    xhr.onerror = () => reject(new ApiError(0, { title: 'Ağ hatası', detail: 'Sunucuya ulaşılamadı.' }));
    xhr.onabort = () => reject(new DOMException('Upload cancelled', 'AbortError'));
    signal?.addEventListener('abort', () => xhr.abort());

    const form = new FormData();
    form.append('file', file, file.name);
    xhr.send(form);
  });
}

/** Message to show for an error from a query or mutation. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const fields = error.fieldErrors;
    return fields.length > 0 ? fields.join(' ') : error.message;
  }

  return error instanceof Error ? error.message : 'Beklenmeyen bir hata oluştu.';
}

/** Downloads a file the API serves behind authorization (a plain link would not carry the bearer token). */
export async function download(path: string, fileName: string): Promise<void> {
  const send = async () => {
    const token = await session.getAccessToken();
    return fetch(`/api/v1${path}`, { headers: token ? { authorization: `Bearer ${token}` } : {} });
  };

  let response = await send();
  if (response.status === 401 && (await session.refresh())) {
    response = await send();
  }

  if (!response.ok) {
    await readResponse(response);
  }

  const url = URL.createObjectURL(await response.blob());
  try {
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
