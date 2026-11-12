import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './tests',
  testMatch: 'human-input.browser.spec.ts',
  workers: 1,
  retries: 0,
  timeout: 30_000,
  reporter: 'list',
  outputDir: './test-results/human-input',
  use: { baseURL: 'http://127.0.0.1:5178', browserName: 'chromium' },
  projects: [
    { name: 'mobile-390', use: { viewport: { width: 390, height: 844 } } },
    { name: 'desktop-1280', use: { viewport: { width: 1280, height: 900 } } },
  ],
  webServer: [
    { command: 'npm run dev -- --host 127.0.0.1 --port 5178 --strictPort', url: 'http://127.0.0.1:5178',
      env: { VITE_AUTH_TOKEN: 'synthetic-operator', VITE_CONFIG_TOKEN: '', VITE_TEMPORAL_UI_URL: '' }, reuseExistingServer: false },
    { command: 'npm run dev -- --host 127.0.0.1 --port 5179 --strictPort', url: 'http://127.0.0.1:5179',
      env: { VITE_AUTH_TOKEN: '', VITE_CONFIG_TOKEN: '', VITE_TEMPORAL_UI_URL: '' }, reuseExistingServer: false },
  ],
})
