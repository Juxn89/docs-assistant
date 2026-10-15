import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { createServer, type Server } from 'node:http';
import type { AddressInfo } from 'node:net';

/**
 * One visitor, the real system: the API built from this repo, Postgres with pgvector, the embedding
 * model and Mailpit (e2e/README.md). The customer's site is a page served from another origin, so CORS
 * is exercised exactly as a browser does it. No language model: answers are extractive, which is what
 * the public demo runs.
 */

const apiUrl = process.env['E2E_API'] ?? 'http://127.0.0.1:5010';
const adminKey = process.env['E2E_ADMIN_KEY'] ?? 'e2e-admin-key-not-a-secret-0123456789abcdef';
const mailpitUrl = process.env['E2E_MAILPIT'] ?? 'http://127.0.0.1:28025';

let server: Server;
let siteOrigin: string;

test.beforeAll(async () => {
	server = createServer((request, response) => {
		const key = new URL(request.url ?? '/', 'http://localhost').searchParams.get('key') ?? '';
		response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
		response.end(`<!doctype html><html lang="en"><head><meta charset="utf-8">
			<meta name="viewport" content="width=device-width, initial-scale=1"><title>Acme store</title></head>
			<body><main><h1>Acme store</h1></main>
			<script src="${apiUrl}/widget.js" data-key="${encodeURIComponent(key)}" defer></script></body></html>`);
	});
	await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
	siteOrigin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

test.afterAll(() => new Promise<void>((resolve) => server.close(() => resolve())));

async function admin(path: string, init: RequestInit = {}): Promise<Response> {
	const response = await fetch(apiUrl + path, { ...init, headers: { 'x-admin-key': adminKey, ...init.headers } });
	if (!response.ok) {
		throw new Error(`${init.method ?? 'GET'} ${path}: ${response.status} ${await response.text()}`);
	}

	return response;
}

async function seedAssistant(handoffEmail: string): Promise<{ id: string; publicKey: string }> {
	const tenant = (await (
		await admin('/v1/admin/tenants', {
			method: 'POST',
			headers: { 'content-type': 'application/json' },
			body: JSON.stringify({ name: 'Acme store', handoffEmail, defaultLanguage: 'English', allowedOrigins: [siteOrigin] }),
		})
	).json()) as { id: string; publicKey: string };

	const form = new FormData();
	const policy = '# Returns\n\nReturns are accepted within 30 days of delivery with the original receipt. Refunds take five business days.';
	form.append('file', new Blob([policy], { type: 'text/markdown' }), 'returns-policy.md');
	await admin(`/v1/admin/tenants/${tenant.id}/documents`, { method: 'POST', body: form });

	await expect
		.poll(async () => ((await (await admin(`/v1/admin/tenants/${tenant.id}/documents`)).json()) as { status: string }[])[0]?.status, { timeout: 120_000 })
		.toBe('Ready');
	return tenant;
}

type MailpitMessage = { ID: string };

test('a visitor asks, reads a cited answer, and reaches a person by email', async ({ page }) => {
	const handoffEmail = `support-${Date.now()}@acme.test`;
	const assistant = await seedAssistant(handoffEmail);

	await page.goto(`${siteOrigin}/?key=${assistant.publicKey}`);
	await page.getByRole('button', { name: 'Open the assistant' }).click();
	await page.getByRole('textbox', { name: 'Your question' }).fill('How many days do I have to return a product?');
	await page.getByRole('textbox', { name: 'Your question' }).press('Enter');

	const log = page.getByRole('log');
	await expect(log).toContainText('Returns are accepted within 30 days', { timeout: 60_000 });
	await expect(log.locator('sup.cite').first()).toHaveText('[1]');
	await expect(log.locator('.sources')).toContainText('returns-policy');

	const accessibility = await new AxeBuilder({ page }).include('docs-assistant-widget').analyze();
	expect(accessibility.violations).toEqual([]);

	await log.getByRole('button', { name: 'Talk to a person' }).click();
	await page.getByRole('textbox', { name: 'Email' }).fill('ana@example.com');
	await page.getByRole('textbox', { name: 'Message' }).fill('I need to return 3 units bought last week.');
	await page.getByRole('button', { name: 'Send to the team' }).click();
	await expect(log).toContainText('Your message reached the team');

	let messages: MailpitMessage[] = [];
	await expect
		.poll(
			async () => {
				const search = await fetch(`${mailpitUrl}/api/v1/search?query=${encodeURIComponent(`to:"${handoffEmail}"`)}`);
				messages = ((await search.json()) as { messages: MailpitMessage[] }).messages;
				return messages.length;
			},
			{ timeout: 60_000 },
		)
		.toBe(1);

	const email = (await (await fetch(`${mailpitUrl}/api/v1/message/${messages[0]!.ID}`)).json()) as {
		Text: string;
		ReplyTo: { Address: string }[];
	};
	expect(email.ReplyTo[0]?.Address).toBe('ana@example.com');
	expect(email.Text).toContain('I need to return 3 units bought last week.');
	expect(email.Text).toContain('How many days do I have to return a product?');

	const leads = (await (await admin(`/v1/admin/tenants/${assistant.id}/leads`)).json()) as { status: string }[];
	expect(leads.map((lead) => lead.status)).toEqual(['Notified']);
});

test('a site that is not registered cannot use the assistant', async ({ page }) => {
	// The assistant exists, but only for another site: the browser's preflight gets no
	// Access-Control-Allow-Origin, so the question never reaches the endpoint.
	const elsewhere = (await (
		await admin('/v1/admin/tenants', {
			method: 'POST',
			headers: { 'content-type': 'application/json' },
			body: JSON.stringify({ name: 'Elsewhere', handoffEmail: 'ops@elsewhere.test', defaultLanguage: 'English', allowedOrigins: ['https://elsewhere.example'] }),
		})
	).json()) as { publicKey: string };

	await page.goto(`${siteOrigin}/?key=${elsewhere.publicKey}`);
	await page.getByRole('button', { name: 'Open the assistant' }).click();
	await page.getByRole('textbox', { name: 'Your question' }).fill('How many days do I have to return a product?');
	await page.getByRole('textbox', { name: 'Your question' }).press('Enter');

	await expect(page.getByRole('alert')).toContainText('Could not reach the assistant');
});
