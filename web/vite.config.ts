/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// В разработке API и хабы проксируются на локальный сервер (dotnet run --project src/ToadFlow.Server.Host).
export default defineConfig({
  plugins: [react()],
  build: {
    rollupOptions: {
      output: {
        // Графики и календари — отдельными чанками: они нужны только сводке, аналитике и формам с датами.
        manualChunks: (id) => {
          if (/node_modules[\/](recharts|@mantine[\/]charts|d3-|victory-vendor)/.test(id)) return 'charts';
          if (/node_modules[\/]@mantine[\/]dates/.test(id)) return 'dates';
          return undefined;
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5000',
      '/hubs': { target: 'http://localhost:5000', ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
});
