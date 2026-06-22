import { json } from '@sveltejs/kit';
import { decodeJwtPayload, isSingleUserMode, readTokens } from '$lib/server/auth';
import type { RequestHandler } from './$types';

export const GET: RequestHandler = ({ cookies }) => {
  const tokens = readTokens(cookies);

  return json({
    singleUserMode: isSingleUserMode(),
    hasSession: Boolean(tokens?.accessToken),
    accessTokenExpiresAt: tokens?.expiresAt,
    idTokenClaims: decodeJwtPayload(tokens?.idToken),
    accessTokenClaims: decodeJwtPayload(tokens?.accessToken)
  });
};
