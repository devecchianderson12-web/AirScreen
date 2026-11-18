# config/

`update-public-key.pem` — clave pública ECDSA P‑256 que DualScreen.exe usa para verificar las actualizaciones.
Se genera una sola vez con `scripts/generate-update-keys.sh` y se versiona. Sin este archivo la compilación
funciona, pero la app se niega a instalar actualizaciones (no puede verificarlas).
