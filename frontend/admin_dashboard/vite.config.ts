import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// The end-to-end tests drive the real Docker stack (start it with `docker compose up -d`), one file at a time.
export default defineConfig({
  plugins: [react()],
  build: { sourcemap: false },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['src/test/setup.ts'],
    testTimeout: 90_000,
    hookTimeout: 30_000,
    fileParallelism: false,
  },
});
