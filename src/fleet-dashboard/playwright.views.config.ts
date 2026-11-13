import { defineConfig } from '@playwright/test'

// The view smoke suite (tests/dashboard-views.browser.spec.ts): the production App against a
// substituted orchestrator. Its own port and output directory, so it can run beside the
// human-input spec.
export default defineConfig({
  testDir: './tests',
  testMatch: 'dashboard-views.browser.spec.ts',
  workers: 4,
  retries: 0,
  timeout: 30_000,
  reporter: 'list',
  outputDir: './test-results/views',
  use: { baseURL: 'http://127.0.0.1:5179', browserName: 'chromium', viewport: { width: 1280, height: 900 } },
  webServer: [
    { command: 'npm run dev -- --host 127.0.0.1 --port 5179 --strictPort', url: 'http://127.0.0.1:5179',
      env: { VITE_TEMPORAL_UI_URL: '' }, reuseExistingServer: false },
  ],
})
