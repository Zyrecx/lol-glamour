import { svelte } from '@sveltejs/vite-plugin-svelte'
import { defineConfig } from 'vitest/config'
import { resolve } from 'node:path'

// Output goes straight into the host's wwwroot, which Kestrel serves. Three pages keep the URLs the server relies on:
// / (app), /login.html (guard redirect target) and /pair.html (the QR opens it).
export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: '../src/Glamour.Host/wwwroot',
    emptyOutDir: true,
    rollupOptions: {
      input: {
        index: resolve(import.meta.dirname, 'index.html'),
        login: resolve(import.meta.dirname, 'login.html'),
        pair: resolve(import.meta.dirname, 'pair.html'),
      },
    },
  },
  // Tests use the browser build of Svelte, so runes like $effect run in tests as in the app (src/lib/vitest-client-env.ts).
  test: { include: ['src/**/*.test.ts'], environment: './src/lib/vitest-client-env.ts' },
})
