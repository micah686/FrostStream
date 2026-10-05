export interface OptionPreset {
  id: number;
  key: string;
  name: string;
  description: string | null;
  ytDlpOptions: Record<string, unknown>;
  createdAt: string;
  lastUpdated: string | null;
}

export interface OptionPresetCreateRequest {
  key: string;
  name: string;
  description: string | null;
  ytDlpOptions: Record<string, unknown>;
}

export interface OptionPresetUpdateRequest {
  name: string;
  description: string | null;
  ytDlpOptions: Record<string, unknown>;
}

const BASE = '/api/user/option-presets';

export async function listOptionPresets(fetchImpl: typeof fetch = fetch): Promise<OptionPreset[]> {
  return getJson<OptionPreset[]>(BASE, fetchImpl);
}

export async function getOptionPreset(key: string, fetchImpl: typeof fetch = fetch): Promise<OptionPreset> {
  return getJson<OptionPreset>(`${BASE}/${encodeURIComponent(key)}`, fetchImpl);
}

export async function createOptionPreset(
  request: OptionPresetCreateRequest,
  fetchImpl: typeof fetch = fetch
): Promise<OptionPreset> {
  return sendJson<OptionPreset>(BASE, 'POST', request, fetchImpl);
}

export async function updateOptionPreset(
  key: string,
  request: OptionPresetUpdateRequest,
  fetchImpl: typeof fetch = fetch
): Promise<OptionPreset> {
  return sendJson<OptionPreset>(`${BASE}/${encodeURIComponent(key)}`, 'PUT', request, fetchImpl);
}

export async function deleteOptionPreset(key: string, fetchImpl: typeof fetch = fetch): Promise<void> {
  const response = await fetchImpl(`${BASE}/${encodeURIComponent(key)}`, {
    method: 'DELETE',
    credentials: 'same-origin'
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `DELETE ${BASE}/${key} failed with status ${response.status}.`));
  }
}

async function getJson<T>(url: string, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, { credentials: 'same-origin' });
  if (!response.ok) {
    throw new Error(await describeError(response, `GET ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
}

async function sendJson<T>(url: string, method: string, body: unknown, fetchImpl: typeof fetch): Promise<T> {
  const response = await fetchImpl(url, {
    method,
    credentials: 'same-origin',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body)
  });

  if (!response.ok) {
    throw new Error(await describeError(response, `${method} ${url} failed with status ${response.status}.`));
  }
  return (await response.json()) as T;
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
