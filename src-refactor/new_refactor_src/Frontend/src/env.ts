import { defineEnvVars } from '@sveltejs/kit/env';

export const variables = defineEnvVars({
	PUBLIC_CAST_BASE_URL: { public: true, schema: (input) => input ?? '' }
});
