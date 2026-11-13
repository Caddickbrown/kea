import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// The build goes straight into the ASP.NET project's wwwroot, which is what the server serves.
export default defineConfig({
  plugins: [svelte()],
  build: { outDir: '../wwwroot', emptyOutDir: true, sourcemap: false },
  server: { proxy: { '/api': 'http://localhost:8080' } },
});
