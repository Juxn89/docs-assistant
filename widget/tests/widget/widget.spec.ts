import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { done, openHostPage, problem, sse, streamed } from './harness';

const launcher = (page: Page) => page.getByRole('button', { name: /open the assistant|abrir el asistente/i });
const question = (page: Page) => page.getByRole('textbox', { name: /your question|tu pregunta/i });
const log = (page: Page) => page.getByRole('log');

async function ask(page: Page, text: string): Promise<void> {
	await question(page).fill(text);
	await question(page).press('Enter');
}

const citedAnswer = sse(
	['delta', { text: 'Returns are accepted ' }],
	['delta', { text: 'within 30 days [1].' }],
	done({
		citations: [
			{ number: 1, title: 'Returns policy', source: 'https://shop.example.com/returns', section: 'Refunds', excerpt: 'Returns are accepted within 30 days…' },
		],
	}),
);

test('streams the answer and shows its sources', async ({ page }) => {
	await openHostPage(page, { chat: (_, route) => streamed(route, citedAnswer) });

	await launcher(page).click();
	await expect(question(page)).toBeFocused();
	await ask(page, 'How long do I have to return it?');

	await expect(log(page)).toContainText('Returns are accepted within 30 days');
	await expect(log(page).locator('sup.cite')).toHaveText('1');
	await expect(log(page).getByLabel('Source 1').first()).toBeVisible();
	await expect(log(page).getByRole('link', { name: 'Returns policy › Refunds' })).toHaveAttribute('href', 'https://shop.example.com/returns');
	await expect(log(page)).toHaveAttribute('aria-busy', 'false');
});

test('model and document text is rendered as text, never as HTML', async ({ page }) => {
	const evil = '<img src=x onerror="window.__pwned=1"><b>bold</b>';
	await openHostPage(page, {
		chat: (_, route) =>
			streamed(
				route,
				sse(
					['delta', { text: evil }],
					done({ citations: [{ number: 1, title: '<i>t</i>', source: 'javascript:alert(1)', section: null, excerpt: evil }] }),
				),
			),
	});

	await launcher(page).click();
	await ask(page, 'hi');

	await expect(log(page)).toContainText('<b>bold</b>');
	await expect(log(page).locator('img, b, i')).toHaveCount(0);
	// A source that is not an http(s) URL is shown, never linked.
	await expect(log(page).getByRole('link')).toHaveCount(0);
	expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
});

test('sends the conversation id back and keeps the chat across page loads', async ({ page }) => {
	const recorded = await openHostPage(page, { chat: (_, route) => streamed(route, citedAnswer) });

	await launcher(page).click();
	await ask(page, 'First question');
	await expect(log(page)).toContainText('within 30 days');
	await ask(page, 'Second question');
	await expect(log(page).locator('.message.assistant')).toHaveCount(2);

	expect(recorded.chat[0]).toMatchObject({ question: 'First question', conversationId: null });
	expect(recorded.chat[1]).toMatchObject({ question: 'Second question', conversationId: '0192f000-0000-7000-8000-000000000001' });

	await page.reload();
	await launcher(page).click();
	await expect(log(page)).toContainText('Second question');
	await expect(log(page).locator('sup.cite').first()).toHaveText('1');
});

test('an abstention offers a person, and the handoff carries the conversation', async ({ page }) => {
	const recorded = await openHostPage(page, {
		chat: (_, route) =>
			streamed(route, sse(['delta', { text: "I can't find that in the documents I have." }], done({ outcome: 'AbstainedNoContext', offerHandoff: true }))),
	});

	await launcher(page).click();
	await ask(page, 'Do you ship to Guatemala?');
	await log(page).getByRole('button', { name: 'Talk to a person' }).click();

	const email = page.getByRole('textbox', { name: 'Email' });
	await expect(email).toBeFocused();
	await expect(page.getByRole('textbox', { name: 'Message' })).toHaveValue('Do you ship to Guatemala?');
	await email.fill('ana@example.com');
	await page.getByRole('textbox', { name: /Name/ }).fill('Ana');
	await page.getByRole('button', { name: 'Send to the team' }).click();

	await expect(log(page)).toContainText('Your message reached the team');
	expect(recorded.handoff[0]).toEqual({
		conversationId: '0192f000-0000-7000-8000-000000000001',
		email: 'ana@example.com',
		name: 'Ana',
		message: 'Do you ship to Guatemala?',
	});
});

test('a rejected handoff explains why and keeps what the visitor wrote', async ({ page }) => {
	await openHostPage(page, { handoff: (_, route) => problem(route, 400, 'handoff.invalid_email') });

	await launcher(page).click();
	await page.getByRole('button', { name: 'Talk to a person' }).first().click();
	await page.getByRole('textbox', { name: 'Email' }).fill('ana@');
	await page.getByRole('textbox', { name: 'Message' }).fill('Call me');
	await page.getByRole('button', { name: 'Send to the team' }).click();

	await expect(page.getByRole('alert')).toHaveText('Enter a valid email address, like ana@example.com.');
	await expect(page.getByRole('textbox', { name: 'Message' })).toHaveValue('Call me');
	await expect(page.getByRole('button', { name: 'Send to the team' })).toBeEnabled();
});

test('rate limiting tells the visitor when to come back', async ({ page }) => {
	await openHostPage(page, { chat: (_, route) => problem(route, 429, 'rate_limited', { 'retry-after': '42' }) });

	await launcher(page).click();
	await ask(page, 'hi');

	await expect(page.getByRole('alert')).toHaveText('Too many questions in a short time. Try again in 42 seconds.');
	await expect(page.getByRole('button', { name: 'Send' })).toBeEnabled();
});

test('an unavailable assistant ends with an error and a way to reach a person', async ({ page }) => {
	await openHostPage(page, { chat: (_, route) => streamed(route, sse(['error', { code: 'assistant.unavailable', message: 'x' }])) });

	await launcher(page).click();
	await ask(page, 'hi');

	await expect(page.getByRole('alert')).toContainText('temporarily unavailable');
	await expect(log(page).getByRole('button', { name: 'Talk to a person' })).toBeVisible();
});

test('the network failing is reported, not swallowed', async ({ page }) => {
	await openHostPage(page, { chat: (_, route) => route.abort('connectionrefused') });

	await launcher(page).click();
	await ask(page, 'hi');

	await expect(page.getByRole('alert')).toContainText('Could not reach the assistant');
});

test('speaks the page language', async ({ page }) => {
	await openHostPage(page, { chat: (_, route) => streamed(route, citedAnswer) }, { lang: 'es-GT' });

	await launcher(page).click();

	await expect(page.getByRole('dialog', { name: 'Asistente' })).toBeVisible();
	await expect(page.getByRole('button', { name: 'Hablar con una persona' })).toBeVisible();
});

test('keyboard: Escape closes and returns focus to the launcher; Shift+Enter is a new line', async ({ page }) => {
	const recorded = await openHostPage(page, { chat: (_, route) => streamed(route, citedAnswer) });

	await launcher(page).focus();
	await page.keyboard.press('Enter');
	await expect(page.getByRole('dialog')).toBeVisible();
	await question(page).pressSequentially('line one');
	await question(page).press('Shift+Enter');
	await expect(question(page)).toHaveValue('line one\n');
	expect(recorded.chat).toHaveLength(0);

	await page.keyboard.press('Escape');
	await expect(page.getByRole('dialog')).toBeHidden();
	await expect(launcher(page)).toBeFocused();
	await expect(launcher(page)).toHaveAttribute('aria-expanded', 'false');
});

test('an empty conversation greets and offers the configured questions', async ({ page }) => {
	const recorded = await openHostPage(
		page,
		{ chat: (_, route) => streamed(route, citedAnswer) },
		{ attributes: { 'data-suggestions': 'How long do I have to return it?|Do you ship abroad?' } },
	);

	await launcher(page).click();
	await expect(log(page)).toContainText('Hi! How can we help?');
	expect((await new AxeBuilder({ page }).include('docs-assistant-widget').analyze()).violations).toEqual([]);
	await page.getByRole('button', { name: 'How long do I have to return it?' }).click();

	await expect(log(page)).toContainText('within 30 days');
	await expect(page.getByRole('button', { name: 'Do you ship abroad?' })).toHaveCount(0);
	expect(recorded.chat[0]).toMatchObject({ question: 'How long do I have to return it?' });
});

test('the host page CSS does not reach into the widget', async ({ page }) => {
	await openHostPage(
		page,
		{},
		{ hostCss: 'body { font-size: 40px; color: rgb(255, 0, 0); } button { background: rgb(255, 0, 0); padding: 50px; } * { font-family: serif; }' },
	);

	const styles = await launcher(page).evaluate((element) => {
		const computed = getComputedStyle(element);
		return { fontSize: computed.fontSize, background: computed.backgroundColor, padding: computed.paddingTop };
	});

	expect(styles).toEqual({ fontSize: '15px', background: 'rgb(15, 118, 110)', padding: '12px' });
});

test('no accessibility violations with an answer and the handoff form open', async ({ page }) => {
	await openHostPage(page, { chat: (_, route) => streamed(route, citedAnswer) });

	await launcher(page).click();
	await ask(page, 'How long do I have to return it?');
	await expect(log(page)).toContainText('within 30 days');
	await page.getByRole('button', { name: 'Talk to a person' }).first().click();

	const results = await new AxeBuilder({ page }).include('docs-assistant-widget').analyze();
	expect(results.violations).toEqual([]);
});

test('takes the whole screen on a phone @mobile', async ({ page }) => {
	await openHostPage(page, {});

	await launcher(page).click();

	// The panel rises in over ~220 ms; measure where it settles.
	const viewport = page.viewportSize();
	await expect
		.poll(() => page.getByRole('dialog').boundingBox())
		.toMatchObject({ x: 0, y: 0, width: viewport?.width, height: viewport?.height });
});
