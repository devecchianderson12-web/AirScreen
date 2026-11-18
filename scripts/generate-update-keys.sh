#!/usr/bin/env bash
# Generates the ECDSA P-256 key pair that signs DualScreen.exe updates.
#  - config/update-public-key.pem  -> commit it (embedded into DualScreen.exe at build time)
#  - update-signing-key.pem        -> store it as the GitHub secret UPDATE_SIGNING_KEY, then delete/keep offline
set -euo pipefail
cd "$(dirname "$0")/.."
if [ -f config/update-public-key.pem ] && grep -q "BEGIN PUBLIC KEY" config/update-public-key.pem; then
  echo "config/update-public-key.pem already exists. Refusing to overwrite (installed copies trust that key)." >&2
  exit 1
fi
umask 077
openssl ecparam -name prime256v1 -genkey -noout -out update-signing-key.pem
openssl ec -in update-signing-key.pem -pubout -out config/update-public-key.pem 2>/dev/null
echo "Public key : config/update-public-key.pem (commit this)"
echo "Private key: update-signing-key.pem -> GitHub secret UPDATE_SIGNING_KEY:"
echo "  gh secret set UPDATE_SIGNING_KEY < update-signing-key.pem"
echo "Then keep an offline backup and delete the local copy."
