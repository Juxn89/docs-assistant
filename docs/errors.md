# Error codes

Every failure the API reports on purpose is an [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)
ProblemDetails with a stable `code` extension. Clients should branch on `code`, never on `title`.

| Code | HTTP | Meaning |
|---|---|---|
| <a id="tenant-not_found"></a>`tenant.not_found` | 404 | The tenant id in the route does not exist. |
| <a id="tenant-name_required"></a>`tenant.name_required` | 400 | A tenant needs a name. |
| <a id="tenant-invalid_email"></a>`tenant.invalid_email` | 400 | The handoff email is not a valid address. |
| <a id="tenant-invalid_origin"></a>`tenant.invalid_origin` | 400 | An allowed origin is not `scheme://host[:port]`. |
| <a id="document-empty"></a>`document.empty` | 400 | The uploaded file has no bytes. |
| <a id="document-too_large"></a>`document.too_large` | 400 | Over the configured upload cap (`Ingestion:MaxFileBytes`, 10 MB by default). |
| <a id="document-unsupported_format"></a>`document.unsupported_format` | 400 | Not PDF, DOCX, Markdown, plain text or HTML (UTF-8). Detected from the bytes, not the name. |
| <a id="document-unsupported_archive"></a>`document.unsupported_archive` | 400 | A ZIP-based file that is not a `.docx`. |
| <a id="url-invalid"></a>`url.invalid` | 400 | Not an absolute http(s) URL. |
| <a id="url-blocked"></a>`url.blocked` | 400 | The URL points at a private, loopback, link-local or reserved address, a non-web port, or carries credentials (SSRF protection). |
| <a id="url-too_large"></a>`url.too_large` | 400 | The resource is larger than `UrlFetch:MaxBytes`. |
| <a id="url-unreachable"></a>`url.unreachable` | 502 | DNS or connection failure. |
| <a id="url-timeout"></a>`url.timeout` | 502 | No answer within `UrlFetch:Timeout`. |
| <a id="url-http_error"></a>`url.http_error` | 502 | The site answered with a non-success status. |
| <a id="url-bad_redirect"></a>`url.bad_redirect` | 502 | A redirect without a `Location` header. |
| <a id="url-too_many_redirects"></a>`url.too_many_redirects` | 502 | More than `UrlFetch:MaxRedirects` hops. |
| <a id="chat-question_required"></a>`chat.question_required` | 400 | The question is empty. |
| <a id="chat-origin_not_allowed"></a>`chat.origin_not_allowed` | 403 | The request's `Origin` is not in the tenant's allowed origins (chat and handoff). |
| <a id="handoff-invalid_email"></a>`handoff.invalid_email` | 400 | The visitor's email is missing, not a plain address (`ana@example.com`, no display name), or contains a line break. |
| <a id="handoff-invalid_name"></a>`handoff.invalid_name` | 400 | The name is longer than 100 characters or spans several lines. |
| <a id="handoff-message_required"></a>`handoff.message_required` | 400 | The message for the team is empty. |
| <a id="handoff-message_too_long"></a>`handoff.message_too_long` | 400 | The message is longer than 2000 characters. |
| <a id="rate_limited"></a>`rate_limited` | 429 | Too many requests from this client or for this assistant; retry after the `Retry-After` header ([ADR-0007](adr/0007-rate-limiting.md)). |

Requests to `/v1/admin/*` without a valid `X-Admin-Key` header get a 401 ProblemDetails.

## Chat stream events

`POST /v1/chat/{publicKey}` answers with `text/event-stream`:

| Event | Data | When |
|---|---|---|
| `delta` | `{ "text": "…" }` | Zero or more times, as the answer is generated. |
| `done` | `{ "conversationId", "outcome", "offerHandoff", "citations": [{ "number", "title", "source", "section", "excerpt" }] }` | Exactly once, last, when the answer completed. Send `conversationId` back with the next question and with a handoff. |
| `error` | `{ "code": "assistant.unavailable", "message" }` | Instead of `done`, when the model cannot be used (not configured, provider failure). |

`outcome` is one of `Answered`, `AnsweredWithoutCitations`, `AnsweredExtractively`, `AbstainedNoContext`,
`AbstainedByModel`, `RejectedByGuardrail`
([ADR-0005](adr/0005-grounded-answers-and-abstention.md), [ADR-0006](adr/0006-no-paid-key-providers-and-extractive-mode.md),
[ADR-0008](adr/0008-prompt-injection-guardrails.md)). `RejectedByGuardrail` means the question tried to
instruct the assistant, or the answer started reproducing its instructions; the streamed text is a
fixed refusal (after any text already shown, if the answer was cut mid-stream) and `offerHandoff` is `true`.

## Handoff

`POST /v1/handoff/{publicKey}` with `{ "conversationId"?, "email", "name"?, "message" }` answers
**202** `{ "leadId" }` as soon as the lead is stored; the email to the operator is sent in the
background and retried if the mail server fails. Sending the same email and message again within ten
minutes returns the same `leadId` instead of a second lead. A `conversationId` that is unknown or
belongs to another assistant is ignored, not an error. Limited to 5 per hour per client
([ADR-0009](adr/0009-human-handoff.md)).
