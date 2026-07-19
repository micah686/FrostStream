import tailwindcss from '@tailwindcss/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

// Matches Ports.Frontend in AppHost (PORT_FRONTEND); PORT is injected when running under
// Aspire/compose so the port is identical across `pnpm run dev`, `aspire run`, and compose.
const port = Number(process.env.PORT ?? 25000);
const webApiUpstream = process.env.WEBAPI_UPSTREAM ?? process.env.WEBAPI_HTTP ?? 'http://localhost:25200';

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
  server: {
    port,
    strictPort: true,
    proxy: {
      '/api': { target: webApiUpstream, changeOrigin: false },
      '/auth': { target: webApiUpstream, changeOrigin: false },
      '/stream': {
        target: webApiUpstream,
        changeOrigin: false,
        rewrite: (path) => path.replace(/^\/stream/, '/api/media/stream')
      }
    }
  },
  preview: { port, strictPort: true }
});
