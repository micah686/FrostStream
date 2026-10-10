import { redirect } from '@sveltejs/kit';

export function load() {
  redirect(307, '/admin/access-control?view=new-policy');
}
