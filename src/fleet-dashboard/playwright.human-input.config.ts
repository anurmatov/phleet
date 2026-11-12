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
  // One server: the page holds no token (#530). Whether it is signed in is the session route's
  // answer, which the spec substitutes.
  webServer: [
    { command: 'npm run dev -- --host 127.0.0.1 --port 5178 --strictPort', url: 'http://127.0.0.1:5178',
      env: { VITE_TEMPORAL_UI_URL: '' }, reuseExistingServer: false },
  ],
})
