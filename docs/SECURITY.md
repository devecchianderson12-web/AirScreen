# Modelo de seguridad

| Amenaza | Mitigación |
|---|---|
| Un desconocido en la misma Wi‑Fi intenta ver la pantalla | Solo dispositivos emparejados (token de 256 bits) o con el código vigente pueden iniciar sesión. El PC no transmite nada antes de autenticar. |
| Fuerza bruta del código | 6 dígitos, caduca en 5 min, un solo uso, se invalida tras 5 fallos. Nunca viaja en claro (HMAC ligado a la huella del certificado). |
| Suplantación del PC / MITM en LAN | TLS con huella del certificado fijada (QR o emparejamiento previo). |
| MITM durante el emparejamiento por código tecleado | El PC no se ha verificado aún (TOFU). El atacante necesitaría el código: la prueba HMAC está ligada a su certificado falso y el código es de un solo uso, pero podría intentar fuerza bruta offline de los 10⁶ códigos y usarlo antes de que caduque. **Para máxima seguridad, empareja con el QR.** |
| Servidor de señalización malicioso | Los PC prueban su identidad firmando un reto; las ofertas SDP (con la huella DTLS) van firmadas y el teléfono las verifica contra la huella fijada. |
| Robo de datos en reposo | Identidad del PC y lista de tokens cifradas con DPAPI; en Android el token se cifra con AES‑GCM del Keystore. Solo se guarda el hash del token en el PC. |
| Dispositivo perdido | *Revocar* en Conexión invalida su token inmediatamente. |
| Actualización manipulada | SHA‑256 + firma ECDSA P‑256 con clave offline; si algo no coincide la descarga se descarta. Reversión automática si la nueva versión no arranca. |
| Elevación de privilegios | La app corre sin privilegios; solo el auxiliar de instalación del controlador se eleva y solo instala el paquete embebido. |

Reporta vulnerabilidades de forma privada mediante *Security advisories* del repositorio.
