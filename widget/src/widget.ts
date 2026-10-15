import { Api, ApiFailure, type Citation, type Done, type Failure } from './api';
import type { Strings } from './i18n';
import { icon } from './icons';
import { answerText, h, sourceList } from './render';
import { css } from './styles';

export type WidgetOptions = {
	api: Api;
	t: Strings;
	/** Scopes what is remembered across page loads to this assistant. */
	storageKey: string;
	accent: string | null;
	title: string | null;
	/** Questions offered before the first one is asked (data-suggestions, "|"-separated). */
	suggestions: string[];
};

type StoredMessage = { role: 'visitor' | 'assistant'; text: string; citations?: Citation[] };

type Stored = { conversationId: string | null; messages: StoredMessage[] };

/** Enough to keep the conversation across page navigation; the server keeps the full record. */
const maxStoredMessages = 30;

/** After this long without a first word, say why it is slow (a cold container, a local model). */
const slowAfterMs = 4000;

/**
 * The chat panel. One instance per page, inside its own shadow root.
 *
 * Accessibility: the launcher is a button that reports whether the panel is open; the panel is a
 * labelled, non-modal dialog that Escape closes (focus returns to the launcher); messages are a
 * polite live log that is marked busy while an answer streams, so a screen reader reads the
 * finished answer once instead of every fragment.
 */
export class Widget {
	private readonly t: Strings;
	private readonly api: Api;
	private readonly storageKey: string;
	private readonly launcher: HTMLButtonElement;
	private readonly panel: HTMLElement;
	private readonly log: HTMLElement;
	private readonly input: HTMLTextAreaElement;
	private readonly send: HTMLButtonElement;
	private readonly welcome: HTMLElement;
	private state: Stored;
	private busy = false;
	private lastQuestion = '';

	constructor(private readonly root: ShadowRoot, options: WidgetOptions) {
		this.t = options.t;
		this.api = options.api;
		this.storageKey = options.storageKey;
		this.state = this.load();

		const style = h('style', {}, css);
		const host = root.host as HTMLElement;
		if (options.accent && CSS.supports('color', options.accent)) {
			host.style.setProperty('--accent', options.accent);
		}

		const title = options.title ?? this.t.title;
		this.launcher = h(
			'button',
			{ class: 'launcher', type: 'button', 'aria-expanded': 'false', 'aria-controls': 'panel', 'aria-label': this.t.open },
			icon('chat', 20),
			h('span', {}, title),
		);
		this.log = h('div', { class: 'log', role: 'log', 'aria-live': 'polite', 'aria-label': title });
		this.input = h('textarea', { id: 'question', rows: '1', maxlength: '1000', placeholder: this.t.placeholder, autocomplete: 'off' });
		this.send = h('button', { class: 'send', type: 'submit', 'aria-label': this.t.send, title: this.t.send }, icon('send', 18));
		this.welcome = this.welcomeCard(options.suggestions);

		const close = h('button', { class: 'close', type: 'button', 'aria-label': this.t.close }, icon('close', 18));
		const ask = h(
			'form',
			{ class: 'ask' },
			h('label', { for: 'question', class: 'visually-hidden' }, this.t.inputLabel),
			h('div', { class: 'composer' }, this.input, this.send),
		);
		const talk = h('button', { class: 'link-button', type: 'button' }, icon('person', 15), this.t.talkToPerson);

		this.panel = h(
			'section',
			{ id: 'panel', class: 'panel', role: 'dialog', 'aria-modal': 'false', 'aria-labelledby': 'title', hidden: true },
			h(
				'header',
				{},
				h('span', { class: 'mark' }, icon('spark', 20)),
				h('div', { class: 'heading' }, h('h2', { id: 'title' }, title), h('p', {}, h('span', { class: 'live' }), this.t.subtitle)),
				close,
			),
			h('div', { class: 'progress', 'aria-hidden': 'true' }),
			this.log,
			ask,
			h('footer', {}, talk, h('span', { class: 'disclaimer' }, this.t.poweredBy)),
		);

		root.append(style, this.launcher, this.panel);

		this.launcher.addEventListener('click', () => this.open());
		close.addEventListener('click', () => this.close());
		talk.addEventListener('click', () => this.showHandoff());
		this.panel.addEventListener('keydown', (event) => {
			if (event.key === 'Escape') {
				this.close();
			}
		});
		ask.addEventListener('submit', (event) => {
			event.preventDefault();
			void this.ask();
		});
		this.input.addEventListener('keydown', (event) => {
			if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
				event.preventDefault();
				void this.ask();
			}
		});

		if (this.state.messages.length === 0) {
			this.log.append(this.welcome);
		}

		for (const message of this.state.messages) {
			this.renderMessage(message);
		}
	}

	/** What an empty conversation shows: who answers, how, and a few questions to start with. */
	private welcomeCard(suggestions: string[]): HTMLElement {
		const chips = suggestions.map((suggestion) => {
			const chip = h('button', { class: 'chip', type: 'button' }, suggestion);
			chip.addEventListener('click', () => void this.ask(suggestion));
			return chip;
		});

		return h(
			'div',
			{ class: 'welcome' },
			h('span', { class: 'mark large' }, icon('spark', 26)),
			h('h3', {}, this.t.greeting),
			h('p', {}, this.t.greetingText),
			chips.length > 0 && h('p', { class: 'chips-label' }, this.t.suggestions),
			chips.length > 0 && h('div', { class: 'chips' }, ...chips),
		);
	}

	open(): void {
		this.panel.hidden = false;
		this.launcher.setAttribute('aria-expanded', 'true');
		this.input.focus();
		this.scrollToEnd();
	}

	close(): void {
		this.panel.hidden = true;
		this.launcher.setAttribute('aria-expanded', 'false');
		this.launcher.focus();
	}

	private async ask(suggested?: string): Promise<void> {
		const question = (suggested ?? this.input.value).trim();
		if (!question || this.busy) {
			return;
		}

		this.setBusy(true);
		this.welcome.remove();
		if (!suggested) {
			this.input.value = '';
		}

		this.lastQuestion = question;
		this.remember({ role: 'visitor', text: question });
		this.renderMessage({ role: 'visitor', text: question });

		const label = h('span', {}, this.t.thinking);
		const status = h('p', { class: 'status' }, h('span', { class: 'dots', 'aria-hidden': 'true' }, h('i'), h('i'), h('i')), label);
		this.log.append(status);
		this.scrollToEnd();
		const slow = window.setTimeout(() => (label.textContent = this.t.slow), slowAfterMs);

		const bubble = h('div', { class: 'message assistant' });
		let text = '';
		let done: Done | null = null;
		const controller = new AbortController();
		try {
			for await (const event of this.api.ask(question, this.state.conversationId, controller.signal)) {
				if (event.type === 'delta') {
					if (!bubble.isConnected) {
						status.replaceWith(bubble);
					}

					text += event.text;
					bubble.textContent = text;
					this.scrollToEnd();
				} else if (event.type === 'done') {
					done = event.done;
				} else {
					this.renderError(this.t.errors.unavailable, true);
				}
			}
		} catch (error) {
			this.renderError(this.describe(error), error instanceof ApiFailure && error.failure.kind !== 'rate_limited');
		} finally {
			window.clearTimeout(slow);
			status.remove();
		}

		if (done) {
			// Re-render the final text with citation markers and the sources it cites.
			bubble.replaceChildren(answerText(text, this.t));
			const sources = sourceList(done.citations, this.t);
			if (sources) {
				bubble.append(sources);
			}

			if (!bubble.isConnected) {
				this.log.append(bubble);
			}

			this.state.conversationId = done.conversationId;
			this.remember({ role: 'assistant', text, citations: done.citations });
			if (done.offerHandoff) {
				this.log.append(this.handoffOffer());
			}
		}

		this.setBusy(false);
		this.scrollToEnd();
		this.input.focus();
	}

	private handoffOffer(): HTMLElement {
		const button = h('button', { class: 'link-button offer', type: 'button' }, icon('person', 15), this.t.talkToPerson);
		button.addEventListener('click', () => {
			button.remove();
			this.showHandoff();
		});
		return button;
	}

	private showHandoff(): void {
		if (this.log.querySelector('form.handoff')) {
			(this.log.querySelector('form.handoff input') as HTMLInputElement | null)?.focus();
			return;
		}

		const email = h('input', { type: 'email', name: 'email', required: true, autocomplete: 'email', maxlength: '320' });
		const name = h('input', { type: 'text', name: 'name', autocomplete: 'name', maxlength: '100' });
		const message = h('textarea', { name: 'message', required: true, rows: '3', maxlength: '2000' });
		message.value = this.lastQuestion;
		const error = h('p', { class: 'alert', role: 'alert' });
		const submit = h('button', { class: 'primary', type: 'submit' }, this.t.submit);
		const cancel = h('button', { class: 'secondary', type: 'button' }, this.t.cancel);

		const form = h(
			'form',
			{ class: 'handoff', 'aria-labelledby': 'handoff-title', novalidate: true },
			h('h3', { id: 'handoff-title' }, this.t.handoffTitle),
			h('p', {}, this.t.handoffIntro),
			h('label', {}, this.t.email, email),
			h('label', {}, h('span', {}, this.t.name, ' ', h('span', { class: 'optional' }, this.t.nameOptional)), name),
			h('label', {}, this.t.message, message),
			error,
			h('div', { class: 'actions' }, submit, cancel),
		);

		cancel.addEventListener('click', () => {
			form.remove();
			this.input.focus();
		});
		form.addEventListener('submit', async (event) => {
			event.preventDefault();
			error.textContent = '';
			submit.disabled = true;
			submit.textContent = this.t.sending;
			try {
				await this.api.handoff({
					conversationId: this.state.conversationId,
					email: email.value.trim(),
					name: name.value.trim() || null,
					message: message.value.trim(),
				});
				form.replaceWith(h('p', { class: 'message assistant done' }, icon('check', 16), this.t.handoffDone));
				this.input.focus();
			} catch (failure) {
				error.textContent = this.describe(failure);
				submit.disabled = false;
				submit.textContent = this.t.submit;
			}
		});

		this.log.append(form);
		this.scrollToEnd();
		email.focus();
	}

	private describe(error: unknown): string {
		if (!(error instanceof ApiFailure)) {
			return this.t.errors.network;
		}

		const failure: Failure = error.failure;
		switch (failure.kind) {
			case 'rate_limited':
				return this.t.errors.rateLimited(failure.retryAfterSeconds);
			case 'network':
				return this.t.errors.network;
			case 'problem':
				return (
					{
						'chat.origin_not_allowed': this.t.errors.originNotAllowed,
						'handoff.invalid_email': this.t.errors.invalidEmail,
						'handoff.invalid_name': this.t.errors.invalidName,
						'handoff.message_required': this.t.errors.messageRequired,
						'handoff.message_too_long': this.t.errors.messageTooLong,
					}[failure.code ?? ''] ?? this.t.errors.generic
				);
		}
	}

	private renderMessage(message: StoredMessage): void {
		if (message.role === 'visitor') {
			this.log.append(h('div', { class: 'message visitor' }, message.text));
			return;
		}

		const bubble = h('div', { class: 'message assistant' }, answerText(message.text, this.t));
		const sources = sourceList(message.citations ?? [], this.t);
		if (sources) {
			bubble.append(sources);
		}

		this.log.append(bubble);
	}

	/** An error in the conversation, optionally followed by the way out: a person. */
	private renderError(text: string, offerHandoff: boolean): void {
		this.log.append(h('p', { class: 'message alert', role: 'alert' }, text));
		if (offerHandoff) {
			this.log.append(this.handoffOffer());
		}
	}

	private setBusy(busy: boolean): void {
		this.busy = busy;
		this.send.disabled = busy;
		this.log.setAttribute('aria-busy', String(busy));
		this.panel.classList.toggle('busy', busy);
	}

	private scrollToEnd(): void {
		this.log.scrollTop = this.log.scrollHeight;
	}

	private remember(message: StoredMessage): void {
		this.state.messages = [...this.state.messages, message].slice(-maxStoredMessages);
		this.save();
	}

	/** sessionStorage can be missing or throw (privacy modes, sandboxed iframes): the chat works without it. */
	private load(): Stored {
		try {
			const raw = sessionStorage.getItem(this.storageKey);
			const parsed = raw ? (JSON.parse(raw) as Stored) : null;
			if (parsed && Array.isArray(parsed.messages)) {
				return parsed;
			}
		} catch {
			// Fall through to a fresh conversation.
		}

		return { conversationId: null, messages: [] };
	}

	private save(): void {
		try {
			sessionStorage.setItem(this.storageKey, JSON.stringify(this.state));
		} catch {
			// Not persisted; the conversation still works for this page.
		}
	}
}
