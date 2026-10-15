/**
 * Lives inside the shadow root: nothing here leaks into the host page and the host's CSS cannot reach
 * in (`all: initial` on the host resets inherited properties such as font and color).
 *
 * Sizes are in px on purpose. Inside a shadow root `rem` still follows the host page's root font size,
 * and sites set it to anything (62.5 % is common), which would shrink or blow up the widget. Browser
 * zoom scales px, so the widget still grows with the user's zoom.
 *
 * One accent color (data-color) drives everything; tints are mixed from it, so a site's brand color
 * is enough to make the widget look native. It should be dark enough for white text on it.
 */
export const css = `
:host {
	all: initial;
	--accent: #0f766e;
	--on-accent: #ffffff;
	--accent-soft: color-mix(in srgb, var(--accent) 10%, transparent);
	--accent-line: color-mix(in srgb, var(--accent) 28%, transparent);
	--bg: #ffffff;
	--surface: #f6f6f4;
	--text: #18181b;
	--muted: #5b5b66;
	--border: #e7e5e4;
	--danger: #b91c1c;
	--shadow: 0 1px 2px rgb(0 0 0 / 0.06), 0 12px 24px -6px rgb(0 0 0 / 0.12), 0 32px 64px -12px rgb(0 0 0 / 0.18);
	position: fixed;
	right: 24px;
	bottom: 24px;
	z-index: 2147483000;
	font: 15px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", sans-serif;
	color: var(--text);
	-webkit-font-smoothing: antialiased;
}

@media (prefers-color-scheme: dark) {
	:host {
		--accent-soft: color-mix(in srgb, var(--accent) 22%, transparent);
		--accent-line: color-mix(in srgb, var(--accent) 45%, transparent);
		--bg: #141416;
		--surface: #1d1d21;
		--text: #f4f4f5;
		--muted: #a1a1aa;
		--border: #2b2b31;
		--danger: #f87171;
		--shadow: 0 1px 2px rgb(0 0 0 / 0.4), 0 24px 48px -12px rgb(0 0 0 / 0.6);
	}
}

*, *::before, *::after { box-sizing: border-box; }

button, input, textarea { font: inherit; color: inherit; }

button { cursor: pointer; }

svg { flex: none; }

:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }

/* ── Launcher ─────────────────────────────────────────────────────────────────── */

.launcher {
	display: inline-flex;
	align-items: center;
	gap: 10px;
	padding: 12px 20px 12px 16px;
	border: 0;
	border-radius: 999px;
	background: var(--accent);
	color: var(--on-accent);
	font-weight: 600;
	letter-spacing: 0.01em;
	box-shadow: 0 10px 28px -8px color-mix(in srgb, var(--accent) 70%, black), 0 2px 6px rgb(0 0 0 / 0.15);
	transition: transform 180ms ease, box-shadow 180ms ease;
}

.launcher:hover { transform: translateY(-2px); }

.launcher[aria-expanded="true"] { display: none; }

/* ── Panel ────────────────────────────────────────────────────────────────────── */

.panel {
	position: relative;
	display: flex;
	flex-direction: column;
	width: 400px;
	height: min(640px, calc(100vh - 48px));
	background: var(--bg);
	border: 1px solid var(--border);
	border-radius: 22px;
	box-shadow: var(--shadow);
	overflow: hidden;
	animation: rise 220ms cubic-bezier(.2, .8, .2, 1);
}

.panel[hidden] { display: none; }

@keyframes rise { from { opacity: 0; transform: translateY(12px) scale(.98); } }

header {
	display: flex;
	align-items: center;
	gap: 12px;
	padding: 16px 14px 14px 18px;
}

.mark {
	display: grid;
	place-items: center;
	width: 38px;
	height: 38px;
	border-radius: 12px;
	background: var(--accent);
	color: var(--on-accent);
	box-shadow: inset 0 0 0 1px rgb(255 255 255 / 0.15);
}

.mark.large { width: 52px; height: 52px; border-radius: 16px; }

.heading { min-width: 0; }

header h2 { margin: 0; font-size: 15.5px; font-weight: 650; letter-spacing: -0.01em; }

header p { display: flex; align-items: center; gap: 7px; white-space: nowrap; margin: 1px 0 0; font-size: 12.5px; color: var(--muted); }

.live {
	width: 7px;
	height: 7px;
	border-radius: 50%;
	background: #22c55e;
	box-shadow: 0 0 0 3px rgb(34 197 94 / 0.2);
}

.close {
	display: grid;
	place-items: center;
	margin-left: auto;
	width: 34px;
	height: 34px;
	border: 0;
	border-radius: 10px;
	background: transparent;
	color: var(--muted);
}

.close:hover { background: var(--surface); color: var(--text); }

/* A hairline under the header that comes alive while an answer is on its way. */
.progress { height: 1px; background: var(--border); position: relative; overflow: hidden; }

.busy .progress::after {
	content: "";
	position: absolute;
	inset: 0;
	width: 40%;
	background: linear-gradient(90deg, transparent, var(--accent), transparent);
	animation: sweep 1.1s ease-in-out infinite;
}

@keyframes sweep { from { transform: translateX(-100%); } to { transform: translateX(250%); } }

/* ── Conversation ─────────────────────────────────────────────────────────────── */

.log {
	flex: 1;
	overflow-y: auto;
	padding: 18px 18px 8px;
	display: flex;
	flex-direction: column;
	gap: 14px;
	scrollbar-width: thin;
}

.welcome { margin: auto 0; padding: 8px 4px 4px; }

.welcome h3 { margin: 14px 0 6px; font-size: 20px; font-weight: 700; letter-spacing: -0.02em; }

.welcome p { margin: 0; color: var(--muted); font-size: 14px; }

.welcome .chips-label { margin-top: 18px; font-size: 12px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.06em; }

.chips { display: grid; gap: 8px; margin-top: 8px; }

.chip {
	text-align: left;
	padding: 10px 14px;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--bg);
	font-size: 14px;
	transition: border-color 150ms ease, background 150ms ease;
}

.chip:hover { border-color: var(--accent-line); background: var(--accent-soft); }

.message { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }

.visitor {
	align-self: flex-end;
	max-width: 85%;
	padding: 9px 14px;
	border-radius: 16px 16px 4px 16px;
	background: var(--accent);
	color: var(--on-accent);
}

.assistant {
	align-self: stretch;
	padding: 2px 2px 2px 14px;
	border-left: 2px solid var(--accent-line);
}

.assistant.done { display: flex; gap: 8px; align-items: flex-start; border-left-color: #22c55e; }

.status { display: flex; align-items: center; gap: 10px; margin: 0; color: var(--muted); font-size: 14px; }

.dots { display: inline-flex; gap: 4px; }

.dots i { width: 6px; height: 6px; border-radius: 50%; background: var(--accent); animation: pulse 1s ease-in-out infinite; }

.dots i:nth-child(2) { animation-delay: .15s; }

.dots i:nth-child(3) { animation-delay: .3s; }

@keyframes pulse { 0%, 100% { opacity: .25; transform: translateY(0); } 50% { opacity: 1; transform: translateY(-2px); } }

.alert { color: var(--danger); }

.message.alert { align-self: stretch; padding: 10px 14px; border-radius: 12px; background: color-mix(in srgb, var(--danger) 10%, transparent); }

.cite {
	display: inline-grid;
	place-items: center;
	min-width: 17px;
	height: 17px;
	margin: 0 1px;
	padding: 0 4px;
	border-radius: 6px;
	background: var(--accent-soft);
	color: var(--accent);
	font-size: 11px;
	font-weight: 700;
	line-height: 1;
	vertical-align: 2px;
}

@media (prefers-color-scheme: dark) {
	.cite { color: var(--text); }
}

/* ── Sources ──────────────────────────────────────────────────────────────────── */

.sources { margin-top: 12px; white-space: normal; }

.sources-title { margin: 0 0 6px; font-size: 11.5px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); }

.sources ol { margin: 0; padding: 0; list-style: none; display: grid; gap: 6px; }

.source {
	display: flex;
	gap: 10px;
	min-width: 0;
	padding: 9px 11px;
	border: 1px solid var(--border);
	border-radius: 12px;
	background: var(--surface);
	font-size: 13px;
}

.source .num {
	display: grid;
	place-items: center;
	width: 20px;
	height: 20px;
	border-radius: 6px;
	background: var(--accent);
	color: var(--on-accent);
	font-size: 11px;
	font-weight: 700;
	flex: none;
}

.source-body { min-width: 0; flex: 1; }

.source-head { display: flex; align-items: center; gap: 6px; min-width: 0; color: var(--muted); }

/* Long "document › section" titles are cut with an ellipsis, never widening the panel. */
.source-title { min-width: 0; color: var(--text); font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

a.source-title { text-decoration: underline; text-decoration-color: var(--accent-line); text-underline-offset: 3px; }

.sources summary { margin-top: 2px; cursor: pointer; color: var(--muted); font-size: 12px; }

.excerpt { margin: 6px 0 0; color: var(--muted); font-size: 12.5px; }

/* ── Actions ──────────────────────────────────────────────────────────────────── */

.link-button {
	display: inline-flex;
	align-items: center;
	gap: 6px;
	white-space: nowrap;
	padding: 0;
	border: 0;
	background: none;
	color: var(--accent);
	font-weight: 600;
	font-size: 14px;
}

.link-button:hover { text-decoration: underline; text-underline-offset: 3px; }

.link-button.offer {
	align-self: flex-start;
	padding: 8px 14px;
	border: 1px solid var(--accent-line);
	border-radius: 999px;
	background: var(--accent-soft);
}

.link-button.offer:hover { text-decoration: none; background: color-mix(in srgb, var(--accent) 16%, transparent); }

@media (prefers-color-scheme: dark) {
	.link-button { color: var(--text); }
}

/* ── Composer ─────────────────────────────────────────────────────────────────── */

form.ask { padding: 10px 14px 6px; }

.composer {
	display: flex;
	align-items: flex-end;
	gap: 8px;
	padding: 6px 6px 6px 14px;
	border: 1px solid var(--border);
	border-radius: 16px;
	background: var(--surface);
	transition: border-color 150ms ease, box-shadow 150ms ease;
}

.composer:focus-within { border-color: var(--accent-line); box-shadow: 0 0 0 4px var(--accent-soft); }

.composer textarea {
	flex: 1;
	min-height: 38px;
	max-height: 120px;
	padding: 8px 0;
	border: 0;
	outline: none;
	resize: none;
	background: transparent;
}

.composer textarea::placeholder { color: var(--muted); }

.send {
	display: grid;
	place-items: center;
	width: 38px;
	height: 38px;
	border: 0;
	border-radius: 12px;
	background: var(--accent);
	color: var(--on-accent);
	transition: transform 150ms ease, opacity 150ms ease;
}

.send:hover { transform: translateY(-1px); }

.send:disabled { opacity: .45; transform: none; cursor: default; }

footer {
	display: flex;
	align-items: center;
	justify-content: space-between;
	gap: 12px;
	padding: 6px 18px 14px;
	font-size: 12px;
	color: var(--muted);
}

footer .link-button { font-size: 12.5px; }

/* Once the conversation itself offers a person, the footer does not repeat it. */
.panel:has(.log .offer) footer .link-button { display: none; }

.panel:has(.log .offer) footer { justify-content: flex-end; }

.disclaimer { text-align: right; }

/* ── Handoff form ─────────────────────────────────────────────────────────────── */

form.handoff {
	display: grid;
	gap: 12px;
	padding: 16px;
	border: 1px solid var(--border);
	border-radius: 16px;
	background: var(--surface);
}

form.handoff h3 { margin: 0; font-size: 16px; letter-spacing: -0.01em; }

form.handoff p { margin: 0; color: var(--muted); font-size: 13.5px; }

form.handoff p.alert { color: var(--danger); }

form.handoff p.alert:empty { display: none; }

form.handoff label { display: grid; gap: 5px; font-size: 13px; font-weight: 600; }

form.handoff .optional { font-weight: 400; color: var(--muted); }

form.handoff input, form.handoff textarea {
	width: 100%;
	padding: 9px 12px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--bg);
	font-weight: 400;
}

form.handoff input:focus, form.handoff textarea:focus { outline: none; border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }

form.handoff .actions { display: flex; gap: 8px; }

.primary, .secondary {
	min-height: 40px;
	padding: 0 16px;
	border-radius: 10px;
	font-weight: 600;
	font-size: 14px;
}

.primary { border: 0; background: var(--accent); color: var(--on-accent); }

.primary:disabled { opacity: .6; cursor: default; }

.secondary { border: 1px solid var(--border); background: transparent; }

.visually-hidden {
	position: absolute;
	width: 1px;
	height: 1px;
	overflow: hidden;
	clip-path: inset(50%);
	white-space: nowrap;
}

@media (max-width: 480px) {
	:host { right: 16px; bottom: 16px; }

	.panel {
		position: fixed;
		inset: 0;
		width: auto;
		height: auto;
		border-radius: 0;
		border: 0;
	}
}

@media (prefers-reduced-motion: reduce) {
	* { transition: none !important; animation: none !important; scroll-behavior: auto !important; }
}
`;
