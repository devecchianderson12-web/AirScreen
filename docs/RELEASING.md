# Publicar versiones

## Configuración inicial (una vez)

1. Crea el repositorio en GitHub y edita `dualscreen.repo.json` (`owner`, `repo`).
2. Genera la clave de firma de actualizaciones:
   ```bash
   ./scripts/generate-update-keys.sh
   gh secret set UPDATE_SIGNING_KEY < update-signing-key.pem
   git add config/update-public-key.pem && git commit -m "Add update public key"
   ```
   Guarda una copia offline de `update-signing-key.pem` y bórrala del disco. **Si la pierdes, las copias
   instaladas no aceptarán nuevas actualizaciones** (tendrías que distribuir un exe nuevo manualmente).
3. Keystore Android (para poder actualizar la APK sobre la anterior):
   ```bash
   base64 -w0 release.jks | gh secret set ANDROID_KEYSTORE_BASE64
   gh secret set ANDROID_KEYSTORE_PASSWORD
   gh secret set ANDROID_KEY_ALIAS
   gh secret set ANDROID_KEY_PASSWORD
   ```
4. (Opcional) Certificado de firma del controlador: `DRIVER_CERT_PFX` (PFX en base64) y `DRIVER_CERT_PASSWORD`.
   Sin él se usa un certificado de prueba (ver DRIVER.md).

| Secret | Obligatorio | Uso |
|---|---|---|
| `UPDATE_SIGNING_KEY` | Sí | Firma ECDSA de `DualScreen.exe` |
| `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD` | Recomendado | Firma de la APK |
| `DRIVER_CERT_PFX`, `DRIVER_CERT_PASSWORD` | Para distribución pública | Firma del catálogo del controlador |

## Cada versión

1. Añade una sección `## [X.Y.Z] - AAAA-MM-DD` en `CHANGELOG.md` (será la nota de la release).
2. Ejecuta:
   ```bash
   ./scripts/new-release.sh X.Y.Z
   ```
   Actualiza la versión, crea el commit y la etiqueta `vX.Y.Z` y los sube.
3. `release.yml` compila el controlador, el exe (con el controlador embebido), la APK, genera `SHA256SUMS`,
   firma el exe, verifica la firma con la clave pública versionada y publica la release con:
   `DualScreen.exe`, `DualScreen.exe.sig`, `DualScreen.apk`, `DualScreen-driver.zip`, `SHA256SUMS`.
4. Las copias instaladas detectan la versión en ≤ 6 h (o al pulsar *Buscar actualizaciones*), muestran la
   notificación y el botón **Actualizar ahora**.

Las versiones siguen SemVer; el `versionCode` de Android se deriva como `X*10000 + Y*100 + Z`.
