#!/usr/bin/env bash
# Usage: verify-update.sh <public-key.pem> <file>  (expects <file>.sig next to it). Same check DualScreen.exe does.
set -euo pipefail
pub="$1"; file="$2"
tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
base64 -d "$file.sig" > "$tmp"
openssl dgst -sha256 -verify "$pub" -signature "$tmp" "$file"
