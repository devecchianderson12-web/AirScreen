#!/usr/bin/env bash
# Usage: sign-update.sh <private-key.pem> <file>  -> writes <file>.sig (base64 DER ECDSA/SHA-256)
set -euo pipefail
key="$1"; file="$2"
openssl dgst -sha256 -sign "$key" -out "$file.sig.der" "$file"
base64 -w0 "$file.sig.der" > "$file.sig"
rm -f "$file.sig.der"
echo "Signed $file"
