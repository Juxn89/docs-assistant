import type { Citation } from './api';
import type { Strings } from './i18n';
import { icon } from './icons';

/**
 * Everything the API returns is text written by a model or taken from a customer's document, so it is
 * only ever inserted as text nodes — never through innerHTML. Links are created only for http(s) URLs.
 */

/** Small DOM helper: an element with attributes and children (strings become text nodes). */
export function h<K extends keyof HTMLElementTagNameMap>(
	tag: K,
	attributes: Record<string, string | boolean> = {},
	...children: (Node | string | null | undefined | false)[]
): HTMLElementTagNameMap[K] {
	const element = document.createElement(tag);
	for (const [name, value] of Object.entries(attributes)) {
		if (value === true) {
			element.setAttribute(name, '');
		} else if (value !== false) {
			element.setAttribute(name, value);
		}
	}

	for (const child of children) {
		if (child) {
			element.append(child);
		}
	}

	return element;
}

const citationMarker = /\[(\d{1,2})\]/g;

/** The answer text, with each [n] marker rendered as a numbered chip labelled "Source n". */
export function answerText(text: string, t: Strings): DocumentFragment {
	const fragment = document.createDocumentFragment();
	let last = 0;
	for (const match of text.matchAll(citationMarker)) {
		fragment.append(text.slice(last, match.index));
		const n = Number(match[1]);
		fragment.append(h('sup', { class: 'cite', 'aria-label': t.source(n), title: t.source(n) }, String(n)));
		last = match.index + match[0].length;
	}

	fragment.append(text.slice(last));
	return fragment;
}

export function safeUrl(source: string): string | null {
	try {
		const url = new URL(source);
		return url.protocol === 'https:' || url.protocol === 'http:' ? url.href : null;
	} catch {
		return null;
	}
}

export function sourceList(citations: Citation[], t: Strings): HTMLElement | null {
	if (citations.length === 0) {
		return null;
	}

	const items = citations.map((citation) => {
		const label = citation.section ? `${citation.title} › ${citation.section}` : citation.title;
		const url = safeUrl(citation.source);
		const title = url
			? h('a', { class: 'source-title', href: url, target: '_blank', rel: 'noopener noreferrer' }, label)
			: h('span', { class: 'source-title' }, label);
		return h(
			'li',
			{ class: 'source' },
			h('span', { class: 'num', 'aria-label': t.source(citation.number) }, String(citation.number)),
			h(
				'div',
				{ class: 'source-body' },
				h('span', { class: 'source-head' }, icon('file', 14), title),
				h('details', {}, h('summary', {}, t.showPassage), h('p', { class: 'excerpt' }, citation.excerpt)),
			),
		);
	});

	return h('div', { class: 'sources' }, h('p', { class: 'sources-title' }, t.sources), h('ol', {}, ...items));
}
