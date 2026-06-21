import { redirect } from '@sveltejs/kit';
import { clearAuthCookies, cookieSecure } from '$lib/server/auth';
import type { RequestHandler } from './$types';

export const GET: RequestHandler = async ({ cookies, url }) => {
  clearAuthCookies(cookies, cookieSecure(url));
  throw redirect(303, '/');
};

export const POST = GET;
