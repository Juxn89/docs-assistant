// Bundles src/index.ts into the API's wwwroot/widget.js and enforces the size budget.
import { build } from 'esbuild';
import { readFile } from 'node:fs/promises';
import { gzipSync } from 'node:zlib';
import { fileURLToPath } from 'node:url';

/** ADR-0010: the widget loads on someone else's page; it must stay small. */
const budgetGzipBytes = 15 * 1024;

const outfile = fileURLToPath(new URL('../../src/DocsAssistant.Api/wwwroot/widget.js', import.meta.url));

await build({
	entryPoints: [fileURLToPath(new URL('../src/index.ts', import.meta.url))],
	outfile,
	bundle: true,
	format: 'iife',
	target: ['es2022'],
	minify: true,
	sourcemap: 'linked',
	legalComments: 'none',
	logLevel: 'warning',
});

const bytes = await readFile(outfile);
const gzipped = gzipSync(bytes, { level: 9 }).length;
console.log(`widget.js: ${(bytes.length / 1024).toFixed(1)} KB, ${(gzipped / 1024).toFixed(1)} KB gzip (budget ${budgetGzipBytes / 1024} KB)`);
if (gzipped > budgetGzipBytes) {
	console.error('widget.js is over its size budget.');
	process.exit(1);
}
