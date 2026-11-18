#!/usr/bin/env bash
# Prints the CHANGELOG.md section for version $1 (e.g. 1.2.3).
set -euo pipefail
v="$1"
awk -v v="$v" '
  $0 ~ "^## \\[?"v"\\]?" {on=1; next}
  on && /^## / {exit}
  on {print}
' "$(dirname "$0")/../CHANGELOG.md"
