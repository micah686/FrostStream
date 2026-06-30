import { apiBaseUrl, cookieSecure, ensureFreshTokens, isSingleUserMode } from '$lib/server/auth';
import type { RequestHandler } from './$types';

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

async function proxy({ request, params, url, cookies }: Parameters<RequestHandler>[0]): Promise<Response> {
  const target = new URL(`/api/${params.path ?? ''}${url.search}`, apiBaseUrl());
  const headers = new Headers();
  request.headers.forEach((value, key) => {
    if (!hopByHopHeaders.has(key.toLowerCase())) {
      headers.set(key, value);
    }
  });

  const anonymousConfigRequest = params.path === 'auth/config';
  if (!isSingleUserMode() && !anonymousConfigRequest) {
    const tokens = await ensureFreshTokens(cookies, cookieSecure(url));
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

export const GET: RequestHandler = proxy;
export const POST: RequestHandler = proxy;
export const PUT: RequestHandler = proxy;
export const PATCH: RequestHandler = proxy;
export const DELETE: RequestHandler = proxy;
