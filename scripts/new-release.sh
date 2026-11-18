#!/usr/bin/env bash
# Usage: scripts/new-release.sh 1.2.3
# Checks the changelog has an entry, updates the version in the Windows project, commits, tags and pushes.
# GitHub Actions (release.yml) then builds and publishes everything.
set -euo pipefail
cd "$(dirname "$0")/.."
v="${1:?version, e.g. 1.2.3}"
[[ "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version must be X.Y.Z"; exit 1; }
grep -q "^## \[$v\]" CHANGELOG.md || { echo "Add a '## [$v]' section to CHANGELOG.md first"; exit 1; }
git diff --quiet || { echo "Working tree not clean"; exit 1; }
sed -i.bak -E "s|<Version>[^<]+</Version>|<Version>$v</Version>|" windows/src/DualScreen/DualScreen.csproj && rm -f windows/src/DualScreen/DualScreen.csproj.bak
git add windows/src/DualScreen/DualScreen.csproj
git commit -m "Release v$v"
git tag -a "v$v" -m "DualScreen v$v"
git push origin HEAD "v$v"
echo "Tag v$v pushed. Follow the build in the Actions tab."
