// Captures the widget on the demo store (widget/demo) against a running stack — same setup as the
// e2e suite (widget/e2e/README.md). The images feed the README artwork (scripts/readme-assets.mjs).
//
//   node scripts/screenshots.mjs [outDir]
import { chromium } from '@playwright/test';
import { createServer } from 'node:http';
import { mkdir, readFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const api = process.env.E2E_API ?? 'http://127.0.0.1:5010';
const adminKey = process.env.E2E_ADMIN_KEY ?? 'e2e-admin-key-not-a-secret-0123456789abcdef';
const outDir = process.argv[2] ?? 'screenshots';
const demo = new URL('../demo/', import.meta.url);
await mkdir(outDir, { recursive: true });

async function admin(path, init = {}) {
	const response = await fetch(api + path, { ...init, headers: { 'x-admin-key': adminKey, ...init.headers } });
	if (!response.ok) throw new Error(`${path}: ${response.status} ${await response.text()}`);
	return response;
}

const template = await readFile(new URL('index.html', demo), 'utf8');
let publicKey = '';
const server = createServer((request, response) => {
	response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
	response.end(template.replaceAll('{{API}}', api).replaceAll('{{KEY}}', publicKey));
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const site = `http://127.0.0.1:${server.address().port}`;

const tenant = await (await admin('/v1/admin/tenants', {
	method: 'POST',
	headers: { 'content-type': 'application/json' },
	body: JSON.stringify({ name: 'Mirlo', handoffEmail: 'hola@mirlo.test', defaultLanguage: 'Spanish', allowedOrigins: [site] }),
})).json();
publicKey = tenant.publicKey;

const docs = fileURLToPath(new URL('docs/', demo));
for (const file of await readdir(docs)) {
	const form = new FormData();
	form.append('file', new Blob([await readFile(docs + file)], { type: 'text/markdown' }), file);
	await admin(`/v1/admin/tenants/${tenant.id}/documents`, { method: 'POST', body: form });
}

for (let i = 0; i < 240; i++) {
	const documents = await (await admin(`/v1/admin/tenants/${tenant.id}/documents`)).json();
	if (documents.every((d) => d.status === 'Ready')) break;
	await new Promise((resolve) => setTimeout(resolve, 500));
}

// Wide enough that the open panel does not cover the store's artwork.
const desktop = { viewport: { width: 1680, height: 1000 }, deviceScaleFactor: 2 };
const phone = { viewport: { width: 390, height: 844 }, deviceScaleFactor: 3, isMobile: true, hasTouch: true };
const browser = await chromium.launch();

async function open(options, colorScheme) {
	const page = await browser.newPage({ ...options, colorScheme });
	await page.goto(site);
	await page.getByRole('button', { name: 'Abrir el asistente' }).click();
	await page.waitForTimeout(400);
	return page;
}

async function answer(page, question) {
	await page.getByRole('button', { name: question }).click();
	await page.getByRole('log').locator('.sources').last().waitFor({ timeout: 60_000 });
	await page.waitForTimeout(400);
}

for (const scheme of ['light', 'dark']) {
	let page = await open(desktop, scheme);
	await page.screenshot({ path: `${outDir}/desktop-welcome-${scheme}.png` });
	await answer(page, '¿El envío es gratis?');
	await page.screenshot({ path: `${outDir}/desktop-answer-${scheme}.png` });
	await page.getByRole('log').getByRole('button', { name: 'Hablar con una persona' }).click();
	await page.getByRole('textbox', { name: 'Email' }).fill('ana@ejemplo.com');
	await page.getByRole('textbox', { name: /Nombre/ }).fill('Ana Morales');
	await page.getByRole('textbox', { name: 'Mensaje' }).fill('¿Hacen envíos a Panamá? Quiero pedir 5 bolsas para mi oficina.');
	await page.getByRole('log').evaluate((log) => (log.scrollTop = log.querySelector('form.handoff').offsetTop - 16));
	await page.screenshot({ path: `${outDir}/desktop-handoff-${scheme}.png` });
	await page.close();

	page = await open(phone, scheme);
	await page.screenshot({ path: `${outDir}/phone-welcome-${scheme}.png` });
	await answer(page, '¿Puedo pausar mi suscripción?');
	await page.screenshot({ path: `${outDir}/phone-answer-${scheme}.png` });
	await page.close();
}

await browser.close();
server.close();
console.log(`Screenshots in ${outDir}/`);
