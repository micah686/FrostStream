import { redirect } from '@sveltejs/kit';
import type { LayoutLoad } from './$types';

export const load: LayoutLoad = async ({ parent }) => {
  const { lite } = await parent();
  if (lite) redirect(307, '/admin');
};
