import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import type { Page, Request, Route } from '@playwright/test';

/** The built bundle (npm run build), exactly what the API serves. */
const widgetJs = readFileSync(fileURLToPath(new URL('../../../src/DocsAssistant.Api/wwwroot/widget.js', import.meta.url)), 'utf8');

export const site = 'https://shop.example.com';
export const api = 'https://api.example.com';
export const key = 'pk_test';

type HostOptions = {
	lang?: string;
	attributes?: Record<string, string>;
	hostCss?: string;
};

const cors = { 'access-control-allow-origin': site, 'access-control-expose-headers': 'retry-after' };

/** SSE body as the API writes it. */
export function sse(...events: [name: string, data: unknown][]): string {
	return events.map(([name, data]) => `event: ${name}\ndata: ${JSON.stringify(data)}\n\n`).join('');
}

export function done(overrides: Record<string, unknown> = {}): [string, unknown] {
	return ['done', { conversationId: '0192f000-0000-7000-8000-000000000001', outcome: 'Answered', offerHandoff: false, citations: [], ...overrides }];
}

export type Recorded = { chat: unknown[]; handoff: unknown[] };

/**
 * Serves a customer page that embeds the widget, and answers the API calls with the given handlers.
 * Returns what the widget sent, for assertions.
 */
export async function openHostPage(
	page: Page,
	handlers: {
		chat?: (body: Record<string, unknown>, route: Route) => Promise<void> | void;
		handoff?: (body: Record<string, unknown>, route: Route) => Promise<void> | void;
	},
	options: HostOptions = {},
): Promise<Recorded> {
	const recorded: Recorded = { chat: [], handoff: [] };
	const attributes = Object.entries({ 'data-key': key, ...options.attributes })
		.map(([name, value]) => `${name}="${value}"`)
		.join(' ');

	await page.route(`${site}/**`, (route) =>
		route.fulfill({
			contentType: 'text/html',
			body: `<!doctype html><html lang="${options.lang ?? 'en'}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Shop</title>
				<style>${options.hostCss ?? ''}</style></head>
				<body><main><h1>Shop</h1><p>Welcome.</p></main>
				<script src="${api}/widget.js" ${attributes} defer></script></body></html>`,
		}),
	);
	await page.route(`${api}/widget.js`, (route) => route.fulfill({ contentType: 'text/javascript', body: widgetJs }));
	await page.route(`${api}/v1/**`, async (route, request) => {
		if (request.method() === 'OPTIONS') {
			return route.fulfill({ status: 204, headers: { ...cors, 'access-control-allow-methods': 'POST', 'access-control-allow-headers': 'content-type' } });
		}

		const body = bodyOf(request);
		if (request.url().includes('/v1/chat/')) {
			recorded.chat.push(body);
			return handlers.chat ? handlers.chat(body, route) : route.fulfill({ status: 500 });
		}

		recorded.handoff.push(body);
		return handlers.handoff ? handlers.handoff(body, route) : route.fulfill({ status: 202, headers: cors, json: { leadId: 'lead-1' } });
	});

	await page.goto(`${site}/`);
	return recorded;
}

export function streamed(route: Route, body: string): Promise<void> {
	return route.fulfill({ status: 200, headers: { ...cors, 'content-type': 'text/event-stream' }, body });
}

export function problem(route: Route, status: number, code: string | null, headers: Record<string, string> = {}): Promise<void> {
	return route.fulfill({ status, headers: { ...cors, ...headers }, json: { title: 'Problem', status, ...(code ? { code } : {}) } });
}

function bodyOf(request: Request): Record<string, unknown> {
	return (request.postDataJSON() as Record<string, unknown> | null) ?? {};
}
