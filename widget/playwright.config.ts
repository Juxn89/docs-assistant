import { defineConfig, devices } from '@playwright/test';

/**
 * Two suites:
 * - widget: the widget alone, every API response scripted with page.route. Fast, deterministic, covers
 *   the failure paths (429, errors, validation) that are hard to provoke on a real server.
 * - stack: the real API, Postgres and Mailpit (see e2e/README.md). One visitor journey end to end.
 */
export default defineConfig({
	testDir: './tests',
	fullyParallel: true,
	forbidOnly: !!process.env['CI'],
	retries: process.env['CI'] ? 1 : 0,
	reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
	use: {
		trace: 'on-first-retry',
		screenshot: 'only-on-failure',
	},
	projects: [
		{ name: 'widget', testDir: './tests/widget', grepInvert: /@mobile/, use: { ...devices['Desktop Chrome'] } },
		{ name: 'widget-mobile', testDir: './tests/widget', grep: /@mobile/, use: { ...devices['Pixel 7'] } },
		{ name: 'stack', testDir: './tests/stack', use: { ...devices['Desktop Chrome'] } },
	],
});
