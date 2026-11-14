import { defineConfig } from '@playwright/test'

// The visual tour (tests/tour.browser.spec.ts). Its own port, so it can run beside the
// human-input suite.
export default defineConfig({
  testDir: './tests',
  testMatch: 'tour.browser.spec.ts',
  workers: 2,
  retries: 0,
  timeout: 30_000,
  reporter: 'list',
  outputDir: './test-results/tour-run',
  // TOUR_THEME=dark draws the same tour in the dark theme (the app follows the system's choice).
  use: { baseURL: 'http://127.0.0.1:5180', browserName: 'chromium', colorScheme: process.env.TOUR_THEME === 'dark' ? 'dark' : 'light' },
  projects: [
    { name: 'phone', use: { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, hasTouch: true } },
    { name: 'desktop', use: { viewport: { width: 1440, height: 900 } } },
  ],
  webServer: [
    { command: 'npm run dev -- --host 127.0.0.1 --port 5180 --strictPort', url: 'http://127.0.0.1:5180',
      env: { VITE_TEMPORAL_UI_URL: '' }, reuseExistingServer: true },
  ],
})
