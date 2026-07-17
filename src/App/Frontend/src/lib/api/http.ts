export class ApiRequestError extends Error {
  constructor(
    message: string,
    readonly status: number
  ) {
    super(message);
    this.name = 'ApiRequestError';
  }
}

let nativeFetch: typeof fetch | null = null;
let csrfToken: string | null = null;
let csrfRequest: Promise<string> | null = null;
let loginNavigationStarted = false;

/** Installs the one browser-wide API boundary used by legacy and new call sites alike. */
export function installApiFetch(): void {
  if (typeof window === 'undefined' || nativeFetch) {
    return;
  }

  nativeFetch = window.fetch.bind(window);
  window.fetch = ((input: RequestInfo | URL, init?: RequestInit) =>
    apiFetch(input, init, nativeFetch!)) as typeof window.fetch;
}

export async function apiFetch(
  input: RequestInfo | URL,
  init: RequestInit = {},
  transport: typeof fetch = nativeFetch ?? fetch
): Promise<Response> {
  const method = (init.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
  const csrfProtected = isUnsafe(method) && isSameOriginApi(input);
  let requestInit: RequestInit = { ...init, credentials: init.credentials ?? 'same-origin' };

  if (csrfProtected) {
    requestInit = withCsrfHeader(requestInit, await getCsrfToken(transport));
  }

  let response = await transport(input, requestInit);
  if (csrfProtected && response.status === 403 && response.headers.get('X-CSRF-Token-Invalid') === 'true') {
    csrfToken = null;
    response = await transport(input, withCsrfHeader(requestInit, await getCsrfToken(transport)));
  }

  if (response.status === 401 && isSameOriginApi(input)) {
    navigateToLoginOnce();
  }

  return response;
}

export async function logout(): Promise<void> {
  const response = await fetch('/auth/logout', { method: 'POST', credentials: 'same-origin' });
  if (!response.ok) {
    throw new ApiRequestError('Sign out failed.', response.status);
  }

  csrfToken = null;
  window.location.assign('/');
}

export async function getJson<T>(url: string, fetchImpl: typeof fetch = fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new ApiRequestError(await describeError(response, `GET ${url} failed with status ${response.status}.`), response.status);
  }
  return (await response.json()) as T;
}

export async function sendJson<T>(
  url: string,
  method: string,
  body: unknown,
  fetchImpl: typeof fetch = fetch
): Promise<T> {
  const response = await fetchImpl(url, {
    method,
    credentials: 'same-origin',
    ...(body === undefined
      ? {}
      : { headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  });

  if (!response.ok) {
    throw new ApiRequestError(await describeError(response, `${method} ${url} failed with status ${response.status}.`), response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export async function sendEmpty(url: string, method: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl(url, { method, credentials: 'same-origin' });
  if (!response.ok) {
    throw new ApiRequestError(await describeError(response, `${method} ${url} failed with status ${response.status}.`), response.status);
  }
}

async function describeError(response: Response, fallback: string): Promise<string> {
  const text = await response.text();
  if (!text) {
    return fallback;
  }

  try {
    const problem = JSON.parse(text) as { title?: string; detail?: string; error?: string; errors?: Record<string, string[]> };
    const validation = problem.errors ? Object.values(problem.errors).flat().join(' ') : '';
    return [problem.title, problem.detail, problem.error, validation].filter(Boolean).join(' - ') || text || fallback;
  } catch {
    return text;
  }
}

function isUnsafe(method: string): boolean {
  return method === 'POST' || method === 'PUT' || method === 'PATCH' || method === 'DELETE';
}

function isSameOriginApi(input: RequestInfo | URL): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  const value = input instanceof Request ? input.url : input.toString();
  const url = new URL(value, window.location.href);
  return url.origin === window.location.origin &&
    (url.pathname.startsWith('/api/') || url.pathname === '/auth/logout');
}

function withCsrfHeader(init: RequestInit, token: string): RequestInit {
  const headers = new Headers(init.headers);
  headers.set('X-CSRF-TOKEN', token);
  return { ...init, headers };
}

async function getCsrfToken(transport: typeof fetch): Promise<string> {
  if (csrfToken) {
    return csrfToken;
  }

  csrfRequest ??= transport('/api/auth/csrf', {
    credentials: 'same-origin',
    cache: 'no-store'
  })
    .then(async (response) => {
      if (!response.ok) {
        throw new ApiRequestError('Unable to obtain a CSRF token.', response.status);
      }
      const body = (await response.json()) as { token: string };
      csrfToken = body.token;
      return body.token;
    })
    .finally(() => {
      csrfRequest = null;
    });

  return csrfRequest;
}

function navigateToLoginOnce(): void {
  if (typeof window === 'undefined' || loginNavigationStarted) {
    return;
  }

  loginNavigationStarted = true;
  const returnTo = `${window.location.pathname}${window.location.search}`;
  window.location.assign(`/auth/login?returnTo=${encodeURIComponent(returnTo)}`);
}
