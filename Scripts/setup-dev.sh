#!/usr/bin/env bash
#
# One-time setup for a fresh clone.
#
# Both settings below live in .git/config rather than in the repository, so they
# cannot be committed and every clone has to opt in. That is git's design, not an
# oversight — a repository cannot be allowed to configure hooks that run
# automatically on your machine.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

# Run the formatting check before every commit, so nothing unformatted is ever
# committed and there is no reformat churn to untangle later.
git config core.hooksPath .githooks
chmod +x .githooks/*

# Skip the mechanical reformat commit in git blame, so blame shows the commit
# that explains a line rather than the one that rewrapped it.
git config blame.ignoreRevsFile .git-blame-ignore-revs

echo "Configured:"
echo "  core.hooksPath       .githooks"
echo "  blame.ignoreRevsFile .git-blame-ignore-revs"
echo

if ! command -v dotnet > /dev/null 2>&1; then
  echo "Warning: dotnet not found — the pre-commit hook needs it." >&2
fi

if ! command -v npx > /dev/null 2>&1; then
  echo "Warning: npx not found — the pre-commit hook needs it." >&2
fi

echo "Restoring formatting tools..."
dotnet tool restore
npm ci

echo
echo "Done. The pre-commit hook will now block unformatted commits."
