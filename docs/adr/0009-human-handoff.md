# ADR-0009 · Human handoff

- **Status:** accepted · 2026-10-14

## Context

The assistant says "I don't know" on purpose (ADR-0005), and the injection guardrails refuse some
questions (ADR-0008). Each of those ends with an offer: *would you like me to pass your question to a
person?* Without a working handoff that offer is a dead end, and for the business buying the
assistant the visitor who could not get an answer is the most valuable one: a sales question, a
problem the documents do not cover.

What the operator needs is the visitor's contact, their message, and what they already asked, so the
reply does not start with "how can I help you?".

## Decision

**Conversations are recorded.** `POST /v1/chat/{key}` accepts an optional `conversationId` and the
`done` event returns it. Each question is stored when it arrives and each answer when it completes
(text, outcome, citations). A conversation is anonymous: it holds no personal data until the visitor
chooses to leave an email. An unknown id (expired, made up, another tenant's) starts a new
conversation instead of failing the question.

**A lead is stored first, the operator is notified after.** `POST /v1/handoff/{key}` validates the
input, stores the lead and answers 202 right away. A background worker sends the email. This is the
same outbox shape as ingestion (ADR-0004): an in-process channel for latency, the table as the source
of truth, a periodic sweep for retries and anything the channel lost, a lease for crashed sends and
`xmin` so two workers cannot claim the same lead. A mail server that is down delays the email and
loses nothing: failures retry with exponential backoff (1, 2, 4… minutes, capped at 30) and after
5 attempts the lead is `Failed` but still listed in `GET /v1/admin/tenants/{id}/leads` with the error.

**The email.** Plain text only: everything in it was written by a visitor, and plain text has nothing
to render. It goes to the tenant's handoff address, in the tenant's language, with the visitor as
**Reply-To** (never as From: their domain did not send it, and SPF/DMARC would reject it). It carries
the last 20 messages of the conversation, oldest first, with the sources each answer cited. The
subject contains the visitor's validated address and none of their free text.

**Abuse.**
- Validation: the address must be a plain `user@domain` with no display name and no line breaks
  (header injection), the name a single line, the message at most 2000 characters.
- Same origin allow-list as the chat.
- Its own rate limit, 5 per hour per client, separate from the chat's (`/v1/handoff/` is its own path
  prefix for that reason): each request puts an email in a real person's inbox.
- A double submit (same email and message within ten minutes) returns the same lead.
- A conversation id from another tenant is not attached: the repository is tenant-filtered, and an
  integration test that removes the filter fails.

**Privacy.** Logs carry lead ids, never the email address or the message.

**Delivery** is a port (`IHandoffNotifier`); SMTP through MailKit is the one adapter. Locally the
AppHost's Mailpit receives everything. Without any SMTP server configured the API still runs, the
sender stays idle with a warning, and leads accumulate where the admin API shows them.

## Consequences

- **At-least-once.** A worker that crashes after the SMTP server accepted the email but before
  recording it sends it again when the lease expires. A duplicate email is the price of never losing a
  request; the lead id in the footer makes duplicates obvious.
- **Conversations are now personal-data adjacent** once a lead links to them. Retention is not
  implemented yet; the demo's data is reset nightly. A real deployment needs a retention period
  (delete conversations older than N days; leads keep their copy of the message and lose the link).
- **One channel.** WhatsApp, a CRM webhook or Slack would be other `IHandoffNotifier` adapters; none
  is built.
- **No CAPTCHA.** The rate limit, the origin check and the dedup bound the abuse for a demo; a real
  deployment with a busy public site may want one in the widget.
