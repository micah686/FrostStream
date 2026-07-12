import { redirect } from '@sveltejs/kit';
import type { PageServerLoad } from './$types';

// /admin has no content of its own; land on the first section. Auth is enforced by +layout.server.ts.
export const load: PageServerLoad = () => {
  throw redirect(307, '/admin/storage');
};
