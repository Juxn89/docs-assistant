# ADR-0010 · The embeddable widget

- **Status:** accepted · 2026-10-15

## Context

The assistant reaches visitors through the customer's own website, which we do not control: any CSS
framework, any JavaScript, any Content Security Policy, any root font size. Whatever runs there must
not break the page, must not be broken by it, must load fast on someone else's Core Web Vitals, and
must be usable with a keyboard and a screen reader.

## Decision

**One script tag, no framework.** `widget.js` is TypeScript bundled by esbuild into a single IIFE,
served by the API itself (`/widget.js`, `Cache-Control: max-age=300`, `Cross-Origin-Resource-Policy:
cross-origin`), so the API host is the only thing a site needs to allow. A framework would weigh more
than the whole widget. Budget: **15 KB gzip, enforced by the build**; it is 6.5 KB.

**Shadow DOM for isolation.** Everything renders in an open shadow root; the host page's selectors do
not match inside it, and `:host { all: initial }` stops inherited font and color from leaking in. A
test loads it on a page that sets `body { font-size: 40px; color: red } button { background: red }`
and checks the widget is untouched. Sizes are in `px` on purpose: inside a shadow root `rem` still
follows the host's root font size, which sites set to anything.

**Text is never HTML.** Answers come from a model and from customers' documents; they are inserted as
text nodes only, `[n]` markers become labelled references, and sources are linked only when they are
`http(s)` URLs. A test streams `<img onerror>` and a `javascript:` source and checks nothing renders
or runs; it fails if the stream is written with `innerHTML`.

**Streaming over fetch.** `EventSource` cannot POST, so the widget reads the SSE response body itself
(events split across chunks, multi-byte characters split across chunks, CRLF).

**CORS per assistant.** A static policy cannot express "each customer's own sites", so a policy
provider builds it from the public key in the path, using the tenant's `allowedOrigins` (cached for
a minute). Admin routes get no policy. The endpoint's own Origin check stays as a second layer.
Two ordering details, both covered by tests that fail when they are undone:
- CORS runs **before** the rate limiter, so a 429 carries `Access-Control-Allow-Origin` and exposes
  `Retry-After`; otherwise the browser hides the status and the widget cannot say "try again in 40 s".
- Preflights (`OPTIONS`) do **not** count against the chat quota (each question would cost two
  permits); they have their own per-client limit, because they read the tenant.

**Accessibility.** The launcher reports `aria-expanded`; the panel is a labelled non-modal dialog that
Escape closes, returning focus to the launcher; the conversation is a `role="log"` live region marked
`aria-busy` while an answer streams, so a screen reader reads the finished answer once instead of every
fragment; errors are `role="alert"`; the handoff form has real labels and autocomplete. axe runs in
the tests with the panel open, an answer shown and the form open: zero violations.

**State.** The conversation id and the last 30 messages are kept in `sessionStorage` per assistant key,
so navigating the site keeps the chat; nothing is kept after the tab closes, and storage being
unavailable (privacy modes, sandboxed frames) only loses that convenience.

**Languages.** English and Spanish: `data-lang`, else the page's `lang`, else the browser's.

## Testing

- `npm test`: the widget against a scripted API in Chromium (desktop and a phone): streaming, citations,
  XSS, conversation id and persistence, handoff and its validation errors, 429 with `Retry-After`, the
  error event, network failure, language, keyboard, host CSS isolation, axe.
- `npm run test:e2e`: a page on another origin, the real API, Postgres, the embedding model and Mailpit.
  The visitor asks, gets a cited answer, asks for a person, and the email arrives with the transcript;
  a page on an unregistered origin is blocked by the browser. Runs in CI (`e2e` job).

Test-only dependencies: Playwright (Apache-2.0) and @axe-core/playwright (MPL-2.0, file-level copyleft,
never shipped in `widget.js`). Versions are pinned exactly and were at least two weeks old when chosen.

## Consequences

- **A site with a CSP must allow the API host** in `script-src` and `connect-src`. The portfolio (whose
  CSP uses nonces) will use its own React component against the same API instead of `widget.js`.
- `widget.js` has a stable URL, so a bad release reaches every site within the cache lifetime. Versioned
  URLs (`/widget.v2.js`) are the way out if the contract ever has to break.
- Chromium only in CI. The widget uses no engine-specific APIs, but Firefox and WebKit are not run.
