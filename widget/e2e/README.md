# End-to-end suite

A browser on a customer's page (another origin) uses the widget against the real API, Postgres with
pgvector, the embedding model and Mailpit. No language model: answers are extractive.

```bash
# from the repository root
docker compose -f widget/e2e/compose.yaml up -d --wait
(cd widget && npm ci && npm run build && npx playwright install chromium)
source widget/e2e/api.env.sh
dotnet run --project src/DocsAssistant.Api --no-launch-profile &   # wait for http://127.0.0.1:5010/health
(cd widget && npm run test:e2e)
docker compose -f widget/e2e/compose.yaml down -v
```

The first run downloads the embedding model (~123 MB). CI runs the same steps (`e2e` job).
`node scripts/screenshots.mjs <dir>` (from `widget/`) captures the images used in the README against
the same running stack.
