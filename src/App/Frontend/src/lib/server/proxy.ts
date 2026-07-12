import type { Cookies } from '@sveltejs/kit';
import { apiBaseUrl, cookieSecure, ensureFreshTokens, isSingleUserMode } from '$lib/server/auth';

const hopByHopHeaders = new Set([
  'connection',
  'keep-alive',
  'proxy-authenticate',
  'proxy-authorization',
  'te',
  'trailer',
  'transfer-encoding',
  'upgrade',
  'host',
  'cookie'
]);

/**
 * Forwards a request to the WebAPI, attaching the session's bearer token in multi-user mode.
 * Used for both the /api JSON surface and the /stream media surface (video elements cannot send
 * Authorization headers themselves, so streaming must round-trip through this BFF too).
 */
export async function proxyRequest(
  request: Request,
  cookies: Cookies,
  targetPath: string,
  options: { anonymous?: boolean } = {}
): Promise<Response> {
  const target = new URL(targetPath, apiBaseUrl());
  const headers = new Headers();
  request.headers.forEach((value, key) => {
    if (!hopByHopHeaders.has(key.toLowerCase())) {
      headers.set(key, value);
    }
  });

  if (!isSingleUserMode() && !options.anonymous) {
    const tokens = await ensureFreshTokens(cookies, cookieSecure(new URL(request.url)));
    if (!tokens?.accessToken) {
      return new Response('Authentication required.', { status: 401 });
    }
    headers.set('authorization', `Bearer ${tokens.accessToken}`);
  }

  const body = request.method === 'GET' || request.method === 'HEAD'
    ? undefined
    : await request.arrayBuffer();
  const response = await fetch(target, {
    method: request.method,
    headers,
    body
  });

  const responseHeaders = new Headers();
  response.headers.forEach((value, key) => {
    if (!hopByHopHeaders.has(key.toLowerCase())) {
      responseHeaders.set(key, value);
    }
  });

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers: responseHeaders
  });
}
