export class ApiRequestError extends Error {
  constructor(
    message: string,
    readonly status: number
  ) {
    super(message);
    this.name = 'ApiRequestError';
  }
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
