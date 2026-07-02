import { proxyRequest } from '$lib/server/proxy';
import type { RequestHandler } from './$types';

const proxy: RequestHandler = ({ request, params, url, cookies }) =>
  proxyRequest(request, cookies, `/api/${params.path ?? ''}${url.search}`, {
    anonymous: params.path === 'auth/config'
  });

export const GET: RequestHandler = proxy;
export const POST: RequestHandler = proxy;
export const PUT: RequestHandler = proxy;
export const PATCH: RequestHandler = proxy;
export const DELETE: RequestHandler = proxy;
