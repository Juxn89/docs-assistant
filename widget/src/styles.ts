/**
 * Lives inside the shadow root: nothing here leaks into the host page and the host's CSS cannot reach
 * in (`all: initial` on the host resets inherited properties such as font and color).
 *
 * Sizes are in px on purpose. Inside a shadow root `rem` still follows the host page's root font size,
 * and sites set it to anything (62.5 % is common), which would shrink or blow up the widget. Browser
 * zoom scales px, so the widget still grows with the user's zoom.
 */
export const css = `
:host {
	all: initial;
	--accent: #0f766e;
	--accent-text: #ffffff;
	--bg: #ffffff;
	--surface: #f4f4f5;
	--text: #18181b;
	--muted: #52525b;
	--border: #d4d4d8;
	--danger: #b91c1c;
	position: fixed;
	right: 20px;
	bottom: 20px;
	z-index: 2147483000;
	font: 15px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
	color: var(--text);
}

@media (prefers-color-scheme: dark) {
	:host {
		--bg: #18181b;
		--surface: #27272a;
		--text: #f4f4f5;
		--muted: #a1a1aa;
		--border: #3f3f46;
		--danger: #f87171;
	}
}

*, *::before, *::after { box-sizing: border-box; }

button, input, textarea { font: inherit; color: inherit; }

:focus-visible { outline: 3px solid var(--accent); outline-offset: 2px; }

.launcher {
	display: inline-flex;
	align-items: center;
	gap: 8px;
	padding: 12px 18px;
	border: 0;
	border-radius: 999px;
	background: var(--accent);
	color: var(--accent-text);
	font-weight: 600;
	box-shadow: 0 6px 20px rgb(0 0 0 / 0.2);
	cursor: pointer;
	transition: transform 150ms ease;
}

.launcher:hover { transform: translateY(-1px); }

.launcher[aria-expanded="true"] { display: none; }

.panel {
	display: flex;
	flex-direction: column;
	width: 380px;
	height: min(600px, calc(100vh - 40px));
	background: var(--bg);
	border: 1px solid var(--border);
	border-radius: 16px;
	box-shadow: 0 12px 40px rgb(0 0 0 / 0.25);
	overflow: hidden;
}

.panel[hidden] { display: none; }

header {
	display: flex;
	align-items: flex-start;
	gap: 12px;
	padding: 14px 16px;
	background: var(--accent);
	color: var(--accent-text);
}

header h2 { margin: 0; font-size: 16px; }

header p { margin: 2px 0 0; font-size: 13px; opacity: 0.9; }

.close {
	margin-left: auto;
	width: 32px;
	height: 32px;
	border: 0;
	border-radius: 8px;
	background: transparent;
	color: inherit;
	font-size: 20px;
	line-height: 1;
	cursor: pointer;
}

.close:hover { background: rgb(255 255 255 / 0.15); }

.log {
	flex: 1;
	overflow-y: auto;
	padding: 16px;
	display: flex;
	flex-direction: column;
	gap: 12px;
}

.message {
	max-width: 88%;
	padding: 10px 12px;
	border-radius: 12px;
	white-space: pre-wrap;
	overflow-wrap: anywhere;
}

.visitor { align-self: flex-end; background: var(--accent); color: var(--accent-text); }

.assistant { align-self: flex-start; background: var(--surface); }

.status { align-self: flex-start; color: var(--muted); font-size: 14px; }

.alert { color: var(--danger); background: transparent; padding: 0; }

.cite { font-size: 11px; color: var(--muted); }

.sources { margin-top: 8px; font-size: 13px; white-space: normal; }

.sources-title { margin: 0 0 4px; font-weight: 600; color: var(--muted); }

.sources ol { margin: 0; padding: 0; list-style: none; display: grid; gap: 6px; }

.sources .num { color: var(--muted); }

.sources a { color: inherit; }

.sources summary { cursor: pointer; color: var(--muted); font-size: 12px; }

.excerpt { margin: 4px 0 0; color: var(--muted); }

.link-button {
	align-self: flex-start;
	padding: 0;
	border: 0;
	background: none;
	color: var(--accent);
	font-weight: 600;
	text-decoration: underline;
	cursor: pointer;
}

@media (prefers-color-scheme: dark) {
	.link-button { color: var(--text); }
}

form.ask {
	display: flex;
	gap: 8px;
	padding: 12px;
	border-top: 1px solid var(--border);
}

textarea, input {
	width: 100%;
	padding: 9px 11px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: var(--bg);
}

form.ask textarea { resize: none; height: 42px; }

.send, .primary {
	padding: 0 16px;
	min-height: 42px;
	border: 0;
	border-radius: 10px;
	background: var(--accent);
	color: var(--accent-text);
	font-weight: 600;
	cursor: pointer;
}

.send:disabled, .primary:disabled { opacity: 0.6; cursor: default; }

form.handoff { display: grid; gap: 10px; padding: 12px; background: var(--surface); border-radius: 12px; }

form.handoff h3 { margin: 0; font-size: 15px; }

form.handoff p { margin: 0; color: var(--muted); font-size: 14px; }

form.handoff label { display: grid; gap: 4px; font-size: 14px; font-weight: 600; }

form.handoff .optional { font-weight: 400; color: var(--muted); }

form.handoff .actions { display: flex; gap: 8px; }

.secondary {
	min-height: 42px;
	padding: 0 14px;
	border: 1px solid var(--border);
	border-radius: 10px;
	background: transparent;
	cursor: pointer;
}

footer { padding: 0 12px 10px; font-size: 12px; color: var(--muted); }

.visually-hidden {
	position: absolute;
	width: 1px;
	height: 1px;
	overflow: hidden;
	clip-path: inset(50%);
	white-space: nowrap;
}

@media (max-width: 480px) {
	:host { right: 12px; bottom: 12px; }

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
