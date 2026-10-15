import { Api } from './api';
import { pickLanguage, strings } from './i18n';
import { Widget } from './widget';

/**
 * Entry point of widget.js. Embedding is one tag:
 *
 *   <script src="https://api.example.com/widget.js" data-key="pk_…" defer></script>
 *
 * Optional: data-lang="es|en", data-title="…", data-color="#0f766e", data-api="https://…" (defaults
 * to the origin the script was loaded from), data-suggestions="First question|Second question" (up to
 * four, shown before the first question).
 */
const maxSuggestions = 4;
const maxSuggestionLength = 80;

function start(script: HTMLScriptElement): void {
	const key = script.dataset['key'];
	if (!key) {
		console.warn('docs-assistant: the script tag needs a data-key attribute.');
		return;
	}

	if (document.querySelector('docs-assistant-widget')) {
		return;
	}

	const apiBase = (script.dataset['api'] ?? new URL(script.src, location.href).origin).replace(/\/+$/, '');
	const host = document.createElement('docs-assistant-widget');
	const root = host.attachShadow({ mode: 'open' });
	document.body.append(host);

	new Widget(root, {
		api: new Api(apiBase, key),
		t: strings[pickLanguage(script.dataset['lang'])],
		storageKey: `docs-assistant:${key}`,
		accent: script.dataset['color'] ?? null,
		title: script.dataset['title'] ?? null,
		suggestions: (script.dataset['suggestions'] ?? '')
			.split('|')
			.map((suggestion) => suggestion.trim())
			.filter((suggestion) => suggestion.length > 0 && suggestion.length <= maxSuggestionLength)
			.slice(0, maxSuggestions),
	});
}

const script = document.currentScript;
if (script instanceof HTMLScriptElement) {
	if (document.body) {
		start(script);
	} else {
		document.addEventListener('DOMContentLoaded', () => start(script), { once: true });
	}
}
