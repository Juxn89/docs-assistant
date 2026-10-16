// Generates the README artwork in docs/readme/: hero banner, metrics strip and feature icons (SVG,
// light and dark), plus the product showcase and the three-step gallery (PNG), composed from the demo
// store screenshots taken by scripts/screenshots.mjs.
//
//   node scripts/screenshots.mjs screenshots          # needs the running stack (e2e/README.md)
//   node scripts/readme-assets.mjs screenshots
//
// GitHub shows README images through <img>: plain SVG, no external fonts or scripts. CSS animations
// inside the SVG do run there, and they stop under prefers-reduced-motion.
import { chromium } from '@playwright/test';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const shots = process.argv[2];
const out = fileURLToPath(new URL('../../docs/readme/', import.meta.url));
await mkdir(out, { recursive: true });

const brand = { teal: '#0f766e', mint: '#2dd4bf', amber: '#f59e0b', amberSoft: '#fcd34d', terracotta: '#a3441f', violet: '#7c3aed' };

const themes = {
	dark: { bg: '#0a0e13', panel: '#121821', card: '#161e29', border: '#223041', text: '#f1f5f9', muted: '#94a3b8', line: '#2a3a4d', dot: '#ffffff', dotOpacity: 0.06, orb: 0.28 },
	light: { bg: '#f8faf9', panel: '#ffffff', card: '#ffffff', border: '#e2e8f0', text: '#0f172a', muted: '#475569', line: '#cbd5e1', dot: '#0f766e', dotOpacity: 0.09, orb: 0.16 },
};

const sans = `'Segoe UI', Inter, -apple-system, BlinkMacSystemFont, 'Helvetica Neue', Arial, sans-serif`;
const mono = `'Cascadia Code', 'SFMono-Regular', Consolas, Menlo, monospace`;
const escape = (text) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function defs(t) {
	return `
	<linearGradient id="brand" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="${brand.mint}"/><stop offset=".6" stop-color="${brand.teal}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<linearGradient id="metric" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${brand.mint}"/><stop offset="1" stop-color="${brand.amber}"/></linearGradient>
	<radialGradient id="orbTeal"><stop offset="0" stop-color="${brand.teal}" stop-opacity="${t.orb}"/><stop offset="1" stop-color="${brand.teal}" stop-opacity="0"/></radialGradient>
	<radialGradient id="orbAmber"><stop offset="0" stop-color="${brand.amber}" stop-opacity="${t.orb * 0.7}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></radialGradient>
	<pattern id="dots" width="22" height="22" patternUnits="userSpaceOnUse"><circle cx="2" cy="2" r="1.1" fill="${t.dot}" fill-opacity="${t.dotOpacity}"/></pattern>`;
}

// ── Hero: what it does, drawn — a document line becomes a cited answer, or a person ─────────────
function hero(name) {
	const t = themes[name];
	const W = 1280;
	const H = 460;
	const chips = ['Hybrid RAG', 'pgvector', 'Evals in CI', 'Guardrails', 'One-line widget'];
	let x = 72;
	const chipSvg = chips
		.map((chip) => {
			const w = chip.length * 8.6 + 30;
			const g = `<g transform="translate(${x} 352)"><rect width="${w}" height="32" rx="16" fill="${t.panel}" stroke="${t.border}"/><text x="${w / 2}" y="21" text-anchor="middle" font-family="${sans}" font-size="14" font-weight="600" fill="${t.text}">${chip}</text></g>`;
			x += w + 8;
			return g;
		})
		.join('');

	const line = (y, w, extra = '') => `<rect x="20" y="${y}" width="${w}" height="7" rx="3.5" fill="${t.line}" ${extra}/>`;

	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-labelledby="title desc">
<title id="title">docs-assistant</title>
<desc id="desc">An AI support assistant that answers from your documents, cites every answer, and hands off to a person when it does not know.</desc>
<style>
	.flow { stroke-dasharray: 6 7; animation: flow 1.6s linear infinite; }
	.pop { opacity: 0; animation: pop .5s ease-out forwards; }
	.glow { animation: glow 2.4s ease-in-out infinite alternate; }
	@keyframes flow { to { stroke-dashoffset: -26; } }
	@keyframes pop { from { opacity: 0; transform: translateY(6px); } to { opacity: 1; transform: none; } }
	@keyframes glow { from { opacity: .55; } to { opacity: 1; } }
	@media (prefers-reduced-motion: reduce) { .flow, .glow { animation: none; } .pop { animation: none; opacity: 1; } }
</style>
<defs>${defs(t)}
	<clipPath id="frame"><rect width="${W}" height="${H}" rx="28"/></clipPath>
	<filter id="soft" x="-20%" y="-20%" width="140%" height="160%"><feDropShadow dx="0" dy="14" stdDeviation="16" flood-color="#000" flood-opacity="${name === 'dark' ? 0.45 : 0.12}"/></filter>
</defs>
<g clip-path="url(#frame)">
	<rect width="${W}" height="${H}" fill="${t.bg}"/>
	<rect width="${W}" height="${H}" fill="url(#dots)"/>
	<circle cx="180" cy="40" r="340" fill="url(#orbTeal)"/>
	<circle cx="1060" cy="470" r="360" fill="url(#orbAmber)"/>
</g>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="28" fill="none" stroke="${t.border}"/>

<g transform="translate(72 70)">
	<rect width="236" height="30" rx="15" fill="${t.panel}" stroke="${t.border}"/>
	<circle cx="18" cy="15" r="4.5" fill="${brand.mint}" class="glow"/>
	<text x="31" y="20" font-family="${sans}" font-size="12.5" font-weight="700" letter-spacing=".1em" fill="${t.muted}">OPEN SOURCE · .NET 10 · MIT</text>
</g>
<text x="68" y="172" font-family="${sans}" font-size="70" font-weight="800" letter-spacing="-.03em" fill="${t.text}">docs<tspan fill="url(#brand)">-assistant</tspan></text>
<text x="72" y="230" font-family="${sans}" font-size="24" font-weight="600" fill="${t.text}">Answers your customers from your documents.</text>
<text x="72" y="268" font-family="${sans}" font-size="20" fill="${t.muted}">Cites every answer. Says “I don’t know” instead of guessing.</text>
<text x="72" y="300" font-family="${sans}" font-size="20" fill="${t.muted}">Hands the conversation to a person when it should.</text>
${chipSvg}

<!-- Document -->
<g transform="translate(724 64)" filter="url(#soft)">
	<rect width="214" height="250" rx="16" fill="${t.card}" stroke="${t.border}"/>
	<text x="20" y="32" font-family="${mono}" font-size="12" fill="${t.muted}">returns-policy.pdf</text>
	<rect x="20" y="48" width="120" height="10" rx="5" fill="${t.text}" fill-opacity=".85"/>
	${line(74, 170)}${line(88, 150)}${line(102, 162)}
	<rect x="12" y="120" width="190" height="38" rx="8" fill="${brand.amber}" fill-opacity="${name === 'dark' ? 0.22 : 0.18}" class="glow"/>
	<rect x="20" y="128" width="168" height="7" rx="3.5" fill="${brand.amber}"/>
	<rect x="20" y="142" width="128" height="7" rx="3.5" fill="${brand.amber}"/>
	${line(176, 158)}${line(190, 170)}${line(204, 120)}${line(218, 146)}
</g>

<!-- The passage travels into the answer -->
<path d="M926 205 C 980 205, 960 128, 1012 128" fill="none" stroke="${brand.amber}" stroke-width="2.2" class="flow"/>
<circle cx="926" cy="205" r="4.5" fill="${brand.amber}"/>

<!-- Chat -->
<g transform="translate(1000 46)" filter="url(#soft)">
	<rect width="236" height="268" rx="18" fill="${t.card}" stroke="${t.border}"/>
	<rect x="18" y="18" width="26" height="26" rx="8" fill="${brand.teal}"/>
	<path d="M31 24l1.6 4.2L37 30l-4.4 1.8L31 36l-1.6-4.2L25 30l4.4-1.8Z" fill="#fff"/>
	<text x="54" y="36" font-family="${sans}" font-size="13" font-weight="700" fill="${t.text}">Assistant</text>
	<g class="pop" style="animation-delay:.2s">
		<rect x="62" y="60" width="158" height="34" rx="12" fill="${brand.teal}"/>
		<text x="76" y="82" font-family="${sans}" font-size="12.5" fill="#fff">Opened bag—refund?</text>
	</g>
	<g class="pop" style="animation-delay:.8s">
		<rect x="18" y="108" width="2" height="74" fill="${brand.mint}" fill-opacity=".6"/>
		<text font-family="${sans}" font-size="12.5" fill="${t.text}">
			<tspan x="30" y="122">Within 14 days we send</tspan>
			<tspan x="30" y="140">another origin at no cost,</tspan>
			<tspan x="30" y="158">once per order.</tspan>
		</text>
		<rect x="122" y="147" width="17" height="16" rx="5" fill="${brand.teal}" fill-opacity=".18"/>
		<text x="130.5" y="159" text-anchor="middle" font-family="${sans}" font-size="10.5" font-weight="800" fill="${name === 'dark' ? brand.mint : brand.teal}">1</text>
		<rect x="30" y="192" width="190" height="30" rx="9" fill="${t.panel}" stroke="${t.border}"/>
		<rect x="38" y="199" width="16" height="16" rx="5" fill="${brand.teal}"/>
		<text x="46" y="211" text-anchor="middle" font-family="${sans}" font-size="10" font-weight="800" fill="#fff">1</text>
		<text x="62" y="211" font-family="${sans}" font-size="11.5" font-weight="600" fill="${t.text}">returns › Opened coffee</text>
	</g>
	<g class="pop" style="animation-delay:1.4s">
		<rect x="18" y="234" width="150" height="22" rx="11" fill="none" stroke="${brand.teal}" stroke-opacity=".5"/>
		<text x="93" y="249" text-anchor="middle" font-family="${sans}" font-size="11" font-weight="700" fill="${name === 'dark' ? brand.mint : brand.teal}">Talk to a person →</text>
	</g>
</g>

<!-- When it cannot answer: a person -->
<path d="M1118 316 C 1118 360, 1060 372, 1020 390" fill="none" stroke="${brand.teal}" stroke-width="2.2" class="flow"/>
<g transform="translate(944 370)">
	<rect width="92" height="40" rx="20" fill="${t.panel}" stroke="${t.border}"/>
	<circle cx="22" cy="20" r="12" fill="${brand.teal}" fill-opacity=".15"/>
	<circle cx="22" cy="16.5" r="4" fill="none" stroke="${name === 'dark' ? brand.mint : brand.teal}" stroke-width="2"/>
	<path d="M15 27c1.5-3.5 12.5-3.5 14 0" fill="none" stroke="${name === 'dark' ? brand.mint : brand.teal}" stroke-width="2" stroke-linecap="round"/>
	<text x="42" y="25" font-family="${sans}" font-size="12.5" font-weight="700" fill="${t.text}">Email</text>
</g>
</svg>
`;
}

// ── Metrics: measured, with the eval that measured them ────────────────────────────────────────
function metrics(name) {
	const t = themes[name];
	const W = 1280;
	const H = 150;
	const items = [
		['98 %', 'retrieval hit@5', '54-question bilingual eval'],
		['12 / 12', 'injection attacks resisted', 'extractive mode'],
		['0', 'real questions refused', 'by the guardrails (0/54)'],
		['9 KB', 'embeddable widget', 'gzip · no framework'],
	];
	const col = W / items.length;
	const cells = items
		.map(
			([value, label, note], i) => `
	<g transform="translate(${col * i + col / 2} 0)">
		<text y="64" text-anchor="middle" font-family="${sans}" font-size="40" font-weight="800" letter-spacing="-.02em" fill="url(#metric)">${escape(value)}</text>
		<text y="94" text-anchor="middle" font-family="${sans}" font-size="16" font-weight="600" fill="${t.text}">${escape(label)}</text>
		<text y="116" text-anchor="middle" font-family="${sans}" font-size="13.5" fill="${t.muted}">${escape(note)}</text>
	</g>${i > 0 ? `<line x1="${col * i}" y1="36" x2="${col * i}" y2="114" stroke="${t.border}"/>` : ''}`,
		)
		.join('');
	return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" role="img" aria-label="98% retrieval hit at 5 on a 54-question bilingual eval. 12 of 12 injection attacks resisted in extractive mode. 0 of 54 real questions refused by the guardrails. 9 KB embeddable widget, gzip, no framework.">
<defs>${defs(t)}</defs>
<rect x=".5" y=".5" width="${W - 1}" height="${H - 1}" rx="22" fill="${t.panel}" stroke="${t.border}"/>
${cells}
</svg>
`;
}

// ── Feature icons: Lucide glyphs (ISC license) on a gradient tile ──────────────────────────────
const glyphs = {
	search: '<path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/><path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/><circle cx="12" cy="12" r="3"/><path d="m16 16-1.9-1.9"/>',
	quote: '<path d="M16 3a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2 1 1 0 0 1 1 1v1a2 2 0 0 1-2 2 1 1 0 0 0-1 1v2a1 1 0 0 0 1 1 6 6 0 0 0 6-6V5a2 2 0 0 0-2-2z"/><path d="M5 3a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2 1 1 0 0 1 1 1v1a2 2 0 0 1-2 2 1 1 0 0 0-1 1v2a1 1 0 0 0 1 1 6 6 0 0 0 6-6V5a2 2 0 0 0-2-2z"/>',
	abstain: '<circle cx="12" cy="12" r="10"/><line x1="9" x2="15" y1="15" y2="9"/>',
	shield: '<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="m9 12 2 2 4-4"/>',
	handoff: '<path d="M3 11h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-5Zm0 0a9 9 0 1 1 18 0m0 0v5a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2h3Z"/><path d="M21 16v2a4 4 0 0 1-4 4h-5"/>',
	widget: '<rect x="2" y="4" width="20" height="16" rx="2"/><path d="M10 4v4"/><path d="M2 8h20"/><path d="M6 4v4"/>',
	tenants: '<path d="M10 12h4"/><path d="M10 8h4"/><path d="M14 21v-3a2 2 0 0 0-4 0v3"/><path d="M6 10H4a2 2 0 0 0-2 2v7a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2h-2"/><path d="M6 21V5a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v16"/>',
	evals: '<path d="M14 2v6a2 2 0 0 0 .245.96l5.51 10.08A2 2 0 0 1 18 22H6a2 2 0 0 1-1.755-2.96l5.51-10.08A2 2 0 0 0 10 8V2"/><path d="M6.453 15h11.094"/><path d="M8.5 2h7"/>',
	offline: '<path d="M10.94 5.274A7 7 0 0 1 15.71 10h1.79a4.5 4.5 0 0 1 4.222 6.057"/><path d="M18.796 18.81A4.5 4.5 0 0 1 17.5 19H9A7 7 0 0 1 5.79 5.78"/><path d="m2 2 20 20"/>',
};

const tiles = {
	search: ['#0f766e', '#2dd4bf'],
	quote: ['#b45309', '#fbbf24'],
	abstain: ['#334155', '#64748b'],
	shield: ['#065f46', '#10b981'],
	handoff: ['#0f766e', '#f59e0b'],
	widget: ['#4338ca', '#818cf8'],
	tenants: ['#0e7490', '#22d3ee'],
	evals: ['#7c2d12', '#f97316'],
	offline: ['#1e3a8a', '#60a5fa'],
};

function iconSvg(name) {
	const [from, to] = tiles[name];
	return `<svg xmlns="http://www.w3.org/2000/svg" width="56" height="56" viewBox="0 0 56 56" aria-hidden="true">
<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient></defs>
<rect width="56" height="56" rx="16" fill="url(#g)"/>
<rect x=".75" y=".75" width="54.5" height="54.5" rx="15.25" fill="none" stroke="#fff" stroke-opacity=".18" stroke-width="1.5"/>
<g transform="translate(16 16)" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${glyphs[name]}</g>
</svg>
`;
}

const divider = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="24" viewBox="0 0 1280 24" aria-hidden="true">
<defs><linearGradient id="d" x1="0" x2="1"><stop offset="0" stop-color="${brand.mint}" stop-opacity="0"/><stop offset=".3" stop-color="${brand.teal}"/><stop offset=".7" stop-color="${brand.amber}"/><stop offset="1" stop-color="${brand.amber}" stop-opacity="0"/></linearGradient></defs>
<rect y="11" width="1280" height="2" rx="1" fill="url(#d)"/>
<rect x="632" y="4" width="16" height="16" rx="5" fill="${brand.teal}"/><text x="640" y="16" text-anchor="middle" font-family="${sans}" font-size="10" font-weight="800" fill="#fff">1</text>
</svg>
`;

for (const name of ['dark', 'light']) {
	await writeFile(`${out}hero-${name}.svg`, hero(name));
	await writeFile(`${out}metrics-${name}.svg`, metrics(name));
}

for (const name of Object.keys(glyphs)) {
	await writeFile(`${out}icon-${name}.svg`, iconSvg(name));
}

await writeFile(`${out}divider.svg`, divider);

if (!shots) {
	console.log(`SVGs written to ${out}. Pass the screenshots directory to compose the PNGs.`);
	process.exit(0);
}

// ── PNGs composed from the demo screenshots ──────────────────────────────────────────────────────
const dataUrl = async (file) => `data:image/png;base64,${(await readFile(`${shots}/${file}`)).toString('base64')}`;
const browser = await chromium.launch();

async function render(html, width, height, file) {
	const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1.5 });
	await page.setContent(html, { waitUntil: 'load' });
	await page.screenshot({ path: `${out}${file}` });
	await page.close();
}

for (const name of ['dark', 'light']) {
	const t = themes[name];
	const base = `* { box-sizing: border-box; margin: 0; } body { width: 100vw; height: 100vh; overflow: hidden; position: relative; background: ${t.bg}; font-family: ${sans}; }
		.dots { position: absolute; inset: 0; background-image: radial-gradient(${t.dot} 1px, transparent 1.2px); background-size: 22px 22px; opacity: ${t.dotOpacity * 1.4}; }
		.orb { position: absolute; border-radius: 50%; filter: blur(100px); }`;

	// Showcase: the store in a browser, the same assistant on a phone.
	const desktop = await dataUrl(`desktop-answer-${name}.png`);
	const phone = await dataUrl(`phone-answer-${name}.png`);
	await render(
		`<!doctype html><html><head><meta charset="utf-8"><style>${base}
		.o1 { width: 700px; height: 700px; left: -200px; top: -260px; background: ${brand.teal}; opacity: ${name === 'dark' ? 0.35 : 0.18}; }
		.o2 { width: 700px; height: 700px; right: -220px; bottom: -300px; background: ${brand.amber}; opacity: ${name === 'dark' ? 0.25 : 0.16}; }
		.window { position: absolute; left: 56px; top: 64px; width: 1150px; border-radius: 16px; overflow: hidden; background: ${t.panel}; border: 1px solid ${t.border};
			box-shadow: 0 50px 100px -20px rgba(0,0,0,${name === 'dark' ? 0.65 : 0.25}); }
		.bar { height: 44px; display: flex; align-items: center; gap: 8px; padding: 0 16px; border-bottom: 1px solid ${t.border}; }
		.d { width: 12px; height: 12px; border-radius: 50%; }
		.url { margin: 0 auto; width: 380px; height: 28px; border-radius: 8px; background: ${t.bg}; border: 1px solid ${t.border}; color: ${t.muted};
			font-size: 13px; display: flex; align-items: center; justify-content: center; gap: 6px; }
		.window img { display: block; width: 100%; }
		.phone { position: absolute; right: 60px; top: 120px; width: 320px; height: 680px; border-radius: 48px; background: #0b0b0c; padding: 11px;
			box-shadow: 0 50px 100px -10px rgba(0,0,0,.55), 0 0 0 2px #2b2b2e; }
		.phone img { width: 100%; height: 100%; object-fit: cover; object-position: top; border-radius: 38px; display: block; }
		</style></head><body><div class="dots"></div><div class="orb o1"></div><div class="orb o2"></div>
		<div class="window"><div class="bar"><span class="d" style="background:#f87171"></span><span class="d" style="background:#fbbf24"></span><span class="d" style="background:#34d399"></span>
			<span class="url">🔒 mirlo-cafe.test</span></div><img src="${desktop}"></div>
		<div class="phone"><img src="${phone}"></div></body></html>`,
		1600,
		920,
		`showcase-${name}.png`,
	);

	// Gallery: the three moments, cropped to the panel. Desktop shots are 1680×1000 CSS px; the panel
	// sits 24 px from the right and bottom edges and measures 400 × 640.
	const steps = [
		['01', 'Ask — or tap a suggestion', `desktop-welcome-${name}.png`],
		['02', 'A cited answer, sources attached', `desktop-answer-${name}.png`],
		['03', 'No answer? Hand off to a person', `desktop-handoff-${name}.png`],
	];
	const cards = (
		await Promise.all(
			steps.map(
				async ([n, caption, file]) => `<figure><figcaption><b>${n}</b>${caption}</figcaption>
				<div class="crop"><img src="${await dataUrl(file)}"></div></figure>`,
			),
		)
	).join('');
	await render(
		`<!doctype html><html><head><meta charset="utf-8"><style>${base}
		.o1 { width: 600px; height: 600px; left: 30%; top: -340px; background: ${brand.teal}; opacity: ${name === 'dark' ? 0.3 : 0.14}; }
		.row { position: absolute; inset: 56px 60px; display: grid; grid-template-columns: repeat(3, 1fr); gap: 40px; }
		figure { display: flex; flex-direction: column; gap: 18px; }
		figcaption { display: flex; align-items: center; gap: 12px; color: ${t.text}; font-size: 19px; font-weight: 600; letter-spacing: -.01em; }
		figcaption b { display: grid; place-items: center; width: 34px; height: 34px; border-radius: 10px; background: ${brand.teal}; color: #fff; font-size: 14px; }
		.crop { position: relative; width: 420px; height: 672px; overflow: hidden; border-radius: 23px;
			box-shadow: 0 40px 80px -24px rgba(0,0,0,${name === 'dark' ? 0.7 : 0.28}); }
		/* Exactly the panel (1256..1656 × 336..976), scaled 1.05. */
		.crop img { position: absolute; width: 1764px; left: -1318.8px; top: -352.8px; }
		</style></head><body><div class="dots"></div><div class="orb o1"></div><div class="row">${cards}</div></body></html>`,
		1500,
		840,
		`gallery-${name}.png`,
	);
}

await browser.close();
console.log(`README artwork written to ${out}`);
