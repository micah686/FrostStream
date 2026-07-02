import { proxyRequest } from '$lib/server/proxy';
import type { RequestHandler } from './$types';

// Media streaming proxy: forwards Range requests to the WebAPI stream endpoint with the
// session's bearer token attached (a <video> element cannot send Authorization headers).
const proxy: RequestHandler = ({ request, params, url, cookies }) =>
  proxyRequest(request, cookies, `/stream/${params.path ?? ''}${url.search}`);

export const GET: RequestHandler = proxy;
export const HEAD: RequestHandler = proxy;
