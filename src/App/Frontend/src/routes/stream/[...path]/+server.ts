import { proxyRequest } from '$lib/server/proxy';
import type { RequestHandler } from './$types';

// HLS streaming proxy: forwards Range requests to the WebAPI stream endpoint with the
// session's bearer token attached (a <video> element cannot send Authorization headers).
// Cast devices instead authenticate each request with a signed castToken query parameter.
const proxy: RequestHandler = ({ request, params, url, cookies }) =>
  proxyRequest(request, cookies, `/api/media/stream/${params.path ?? ''}${url.search}`, {
    anonymous: url.searchParams.has('castToken')
  });

export const GET: RequestHandler = proxy;
export const HEAD: RequestHandler = proxy;
