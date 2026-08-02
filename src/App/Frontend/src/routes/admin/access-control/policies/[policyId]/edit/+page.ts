import { redirect } from '@sveltejs/kit';

export function load({ params }) {
  redirect(307, `/admin/access-control?view=edit-policy&policyId=${encodeURIComponent(params.policyId)}`);
}
