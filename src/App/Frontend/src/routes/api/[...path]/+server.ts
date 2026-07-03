import { proxyRequest } from '$lib/server/proxy';
import type { RequestHandler } from './$types';

const proxy: RequestHandler = ({ request, params, url, cookies }) =>
  proxyRequest(request, cookies, `/api/${params.path ?? ''}${url.search}`, {
    // Cast devices fetch media with a signed castToken and no session cookies; the
    // WebAPI authenticates the token itself, so don't require a session here.
    anonymous: params.path === 'auth/config' || url.searchParams.has('castToken')
  });

export const GET: RequestHandler = proxy;
export const POST: RequestHandler = proxy;
export const PUT: RequestHandler = proxy;
export const PATCH: RequestHandler = proxy;
export const DELETE: RequestHandler = proxy;
