// Captures the widget against a running stack (same setup as the e2e suite), for the README and the
// Workana listing: node scripts/screenshots.mjs [outDir]
import { chromium } from '@playwright/test';
import { createServer } from 'node:http';
import { mkdir } from 'node:fs/promises';

const api = process.env.E2E_API ?? 'http://127.0.0.1:5010';
const adminKey = process.env.E2E_ADMIN_KEY ?? 'e2e-admin-key-not-a-secret-0123456789abcdef';
const outDir = process.argv[2] ?? 'screenshots';
await mkdir(outDir, { recursive: true });

const server = createServer((request, response) => {
	const url = new URL(request.url ?? '/', 'http://localhost');
	response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
	response.end(`<!doctype html><html lang="${url.searchParams.get('lang') ?? 'en'}"><head><meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1"><title>Acme</title>
		<style>body{margin:0;font-family:Georgia,serif;background:#faf7f2;color:#222}main{max-width:720px;margin:64px auto;padding:0 24px}</style></head>
		<body><main><h1>Acme Outdoor</h1><p>Tents, backpacks and everything for the trail.</p></main>
		<script src="${api}/widget.js" data-key="${url.searchParams.get('key')}" defer></script></body></html>`);
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const site = `http://127.0.0.1:${server.address().port}`;

async function admin(path, init = {}) {
	const response = await fetch(api + path, { ...init, headers: { 'x-admin-key': adminKey, ...init.headers } });
	if (!response.ok) throw new Error(`${path}: ${response.status}`);
	return response;
}

const tenant = await (await admin('/v1/admin/tenants', {
	method: 'POST',
	headers: { 'content-type': 'application/json' },
	body: JSON.stringify({ name: 'Acme', handoffEmail: 'support@acme.test', defaultLanguage: 'English', allowedOrigins: [site] }),
})).json();
const form = new FormData();
form.append('file', new Blob(['# Returns\n\nReturns are accepted within 30 days of delivery with the original receipt. Refunds take five business days.\n\n# Shipping\n\nShipping is free on orders over 50 dollars.'], { type: 'text/markdown' }), 'customer-policies.md');
await admin(`/v1/admin/tenants/${tenant.id}/documents`, { method: 'POST', body: form });
for (let i = 0; i < 120; i++) {
	const [document] = await (await admin(`/v1/admin/tenants/${tenant.id}/documents`)).json();
	if (document.status === 'Ready') break;
	await new Promise((resolve) => setTimeout(resolve, 500));
}

const browser = await chromium.launch();
for (const [name, options] of [
	['desktop-light', { viewport: { width: 1280, height: 800 }, colorScheme: 'light' }],
	['desktop-dark', { viewport: { width: 1280, height: 800 }, colorScheme: 'dark' }],
	['mobile', { viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true }],
]) {
	const page = await browser.newPage(options);
	await page.goto(`${site}/?key=${tenant.publicKey}`);
	await page.getByRole('button', { name: 'Open the assistant' }).click();
	await page.getByRole('textbox', { name: 'Your question' }).fill('How long do I have to return something?');
	await page.getByRole('textbox', { name: 'Your question' }).press('Enter');
	await page.getByRole('log').locator('.sources').waitFor();
	await page.screenshot({ path: `${outDir}/${name}.png` });
	await page.close();
}

await browser.close();
server.close();
console.log(`Screenshots in ${outDir}/`);
