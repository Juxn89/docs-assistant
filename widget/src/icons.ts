/**
 * The few glyphs the widget draws, as path data on a 24×24 grid (stroke style, after Lucide, ISC).
 * Built with createElementNS: no markup strings, so nothing here goes through an HTML parser.
 */
const paths = {
	chat: ['M21 11.5a8.4 8.4 0 0 1-12.2 7.5L3 21l2-5.6A8.5 8.5 0 1 1 21 11.5Z'],
	spark: ['M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9Z', 'M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8Z'],
	close: ['M18 6 6 18', 'm6 6 12 12'],
	send: ['M12 19V5', 'm5 12 7-7 7 7'],
	person: ['M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2', 'M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z'],
	file: ['M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z', 'M14 3v5h5'],
	check: ['M20 6 9 17l-5-5'],
} as const;

export type IconName = keyof typeof paths;

const ns = 'http://www.w3.org/2000/svg';

export function icon(name: IconName, size = 18): SVGSVGElement {
	const svg = document.createElementNS(ns, 'svg');
	for (const [attribute, value] of Object.entries({
		width: String(size),
		height: String(size),
		viewBox: '0 0 24 24',
		fill: 'none',
		stroke: 'currentColor',
		'stroke-width': '2',
		'stroke-linecap': 'round',
		'stroke-linejoin': 'round',
		'aria-hidden': 'true',
		focusable: 'false',
	})) {
		svg.setAttribute(attribute, value);
	}

	for (const d of paths[name]) {
		const path = document.createElementNS(ns, 'path');
		path.setAttribute('d', d);
		svg.append(path);
	}

	return svg;
}
