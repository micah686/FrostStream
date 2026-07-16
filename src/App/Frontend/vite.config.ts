import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

// Matches Ports.Frontend in AppHost (PORT_FRONTEND); PORT is injected when running under
// Aspire/compose so the port is identical across `pnpm run dev`, `aspire run`, and compose.
const port = Number(process.env.PORT ?? 25000);

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  resolve: {
    alias: [
      {
        // This also applies inside JASSUB's module worker.
        find: /^lfa-ponyfill$/,
        replacement: fileURLToPath(new URL('./src/lib/jassub/google-fonts.ts', import.meta.url))
      }
    ]
  },
  server: { port, strictPort: true },
  preview: { port, strictPort: true }
});
