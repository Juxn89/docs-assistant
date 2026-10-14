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

Requests to `/v1/admin/*` without a valid `X-Admin-Key` header get a 401 ProblemDetails.
