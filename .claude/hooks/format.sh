#!/usr/bin/env bash
# PostToolUse hook: after Claude edits a C# file, format exactly that file.
# CLAUDE.md asks for tabs; this hook makes it true. A rule an agent can forget is a wish, a hook is a rule.
set -euo pipefail

file=$(python -c "import sys, json; print(json.load(sys.stdin).get('tool_input', {}).get('file_path', ''))" 2>/dev/null || true)

case "$file" in
	*.cs)
		dotnet format whitespace DocsAssistant.slnx --include "$file" >/dev/null 2>&1 || true
		;;
esac
