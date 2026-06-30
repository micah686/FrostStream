import type { Cookies } from '@sveltejs/kit';
import { vi } from 'vitest';

export interface RecordedCookie {
  value: string;
  options: Record<string, unknown>;
}

/**
 * Minimal in-memory {@link Cookies} stand-in covering the surface the BFF handlers use
 * (get/set/delete) plus inspection of the options each cookie was written/deleted with.
 */
export class FakeCookies implements Partial<Cookies> {
  readonly store = new Map<string, RecordedCookie>();
  readonly deleted = new Map<string, Record<string, unknown>>();

  constructor(initial: Record<string, string> = {}) {
    for (const [name, value] of Object.entries(initial)) {
      this.store.set(name, { value, options: {} });
    }
  }

  get = (name: string): string | undefined => this.store.get(name)?.value;

  set = (name: string, value: string, options: Record<string, unknown> = {}): void => {
    this.store.set(name, { value, options });
  };

  delete = (name: string, options: Record<string, unknown> = {}): void => {
    this.store.delete(name);
    this.deleted.set(name, options);
  };

  recorded(name: string): RecordedCookie | undefined {
    return this.store.get(name);
  }
}

/** JSON `Response` for a stubbed `fetch`. */
export function jsonResponse(body: unknown, init: ResponseInit = {}): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'content-type': 'application/json' },
    ...init
  });
}

export const discoveryDocument = {
  authorization_endpoint: 'https://authentik.test/application/o/authorize/',
  token_endpoint: 'https://authentik.test/application/o/token/',
  end_session_endpoint: 'https://authentik.test/application/o/froststream/end-session/'
};

/**
 * Installs a `fetch` mock that routes by URL substring. Each route returns a `Response` (or throws).
 * The returned spy lets tests assert on the captured requests.
 */
export function stubFetch(routes: Array<{ match: string; respond: (url: string, init?: RequestInit) => Response }>) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof URL ? input.toString() : typeof input === 'string' ? input : input.url;
    const route = routes.find((r) => url.includes(r.match));
    if (!route) {
      throw new Error(`Unexpected fetch to ${url}`);
    }
    return route.respond(url, init);
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

/** Resolves the thrown SvelteKit redirect/error into something assertable. */
export async function capture(run: () => Promise<unknown>): Promise<{ status: number; location?: string; body?: string }> {
  try {
    await run();
    throw new Error('Expected the handler to throw a redirect or error.');
  } catch (thrown) {
    const value = thrown as { status?: number; location?: string; body?: { message?: string } };
    return {
      status: value.status ?? 0,
      location: value.location,
      body: value.body?.message
    };
  }
}
