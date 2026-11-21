# Controlador de pantalla virtual (DualScreenIdd)

## Por qué hace falta un controlador

Para que Windows reconozca el teléfono como **monitor real** (Configuración → Pantalla, modo extendido,
arrastrar ventanas, frecuencias propias) es obligatorio un *Indirect Display Driver*. No existe ninguna API
de modo usuario que cree monitores. Por eso:

- `DualScreen.exe` sigue siendo **portable**: lleva el paquete del controlador embebido y lo instala solo
  cuando el usuario lo pide (Configuración → Controlador → Instalar), con **un** aviso UAC.
- El **modo espejo** funciona sin controlador.

La instalación (`DualScreen.exe --driver install`, elevado) hace lo mismo que `devcon install`:
`pnputil /add-driver` → crea el nodo raíz `Root\DualScreenIdd` → `UpdateDriverForPlugAndPlayDevices`.
La desinstalación elimina el nodo y el paquete del almacén de controladores.

## Firma: lo que realmente se necesita

Windows 10/11 de 64 bits solo carga controladores firmados de forma confiable. Hay tres caminos:

| Opción | Funciona en | Requisitos | Coste |
|---|---|---|---|
| **A. Firma de prueba** (por defecto en CI si no hay certificado) | Equipos con *test signing* activado y Secure Boot desactivado | Nada | Gratis |
| **B. Firma de atestación de Microsoft** (recomendada para distribuir) | Windows 10/11 normales | Certificado **EV** de firma de código + cuenta del *Hardware Developer Program* (Partner Center) | Certificado EV (~250–600 USD/año); el programa es gratuito |
| C. Certificación WHQL (HLK) | Todos, incluido Windows Update | Igual que B + pasar las pruebas HLK | Tiempo |

No hay forma legítima de evitar esto; "ocultarlo" o desactivar protecciones del usuario no es aceptable.

### Opción A — Certificado propio (predeterminada)
DualScreenIdd es un controlador de **modo usuario** (UMDF 2 / IddCx). Desde la versión 0.2.1, *Instalar* agrega el
certificado `DualScreenTest.cer` que acompaña al paquete a **Entidades de certificación raíz de confianza** y
**Editores de confianza** del equipo (con el mismo aviso UAC) y luego instala el controlador: no hace falta modo de
prueba ni desactivar Secure Boot. *Desinstalar* quita esos certificados.

Si aun así Windows lo rechazara (directivas de empresa, HVCI estricto), usa el procedimiento manual:

1. Abre DualScreen y pulsa Configuración → Controlador → Instalar una vez (fallará por la firma, pero extrae el
   paquete). El certificado queda en `%LOCALAPPDATA%\DualScreen\driver\package\DualScreenTest.cer`.
2. Desactiva Secure Boot en la BIOS/UEFI.
3. PowerShell como administrador:
   ```powershell
   .\scripts\install-test-certificate.ps1 -Certificate "$env:LOCALAPPDATA\DualScreen\driver\package\DualScreenTest.cer"
   ```
   (Confía en el certificado y ejecuta `bcdedit /set testsigning on`.) Reinicia.
4. En DualScreen: Configuración → Controlador → Instalar.

Windows mostrará la marca de agua "Modo de prueba". Para revertir: `bcdedit /set testsigning off`.

### Opción B — Firma de atestación (distribución pública)
1. Consigue un certificado EV de firma de código y regístrate en el *Windows Hardware Developer Program*.
2. Compila el paquete (`msbuild virtual-display-driver\DualScreenIdd.sln /p:Configuration=Release /p:Platform=x64`).
3. Firma el `.cab` del paquete con tu certificado EV:
   ```powershell
   makecab /f scripts\driver.ddf   # o empaqueta DualScreenIdd.inf, .dll y .cat en un CAB
   signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 /a DualScreenIdd.cab
   ```
4. Partner Center → *Hardware* → *Submit new hardware* → sube el CAB, marca Windows 10/11 x64, *attestation*.
5. Descarga el paquete firmado por Microsoft y colócalo en `build/driver/` antes de publicar el exe
   (o guárdalo en un *release asset* y apunta el flujo a él).

Guarda el certificado EV (PFX en base64) en los *secrets* `DRIVER_CERT_PFX` / `DRIVER_CERT_PASSWORD` para que
`release.yml` firme el catálogo automáticamente.

## Contrato IOCTL

Definido en `virtual-display-driver/DualScreenIdd/Public.h` y `windows/src/DualScreen/Display/DriverClient.cs`.

| IOCTL | Entrada | Salida |
|---|---|---|
| `IOCTL_DS_GET_VERSION` (0x800) | — | `UINT32` versión de interfaz |
| `IOCTL_DS_ADD_MONITOR` (0x801) | `DS_ADD_MONITOR` (conector, hasta 32 modos ancho/alto/Hz, preferido) | — |
| `IOCTL_DS_REMOVE_MONITOR` (0x802) | `UINT32` conector | — |
| `IOCTL_DS_REMOVE_ALL` (0x803) | — | — |

Cada monitor lleva un EDID con fabricante `DSC`, producto `0001`, número de serie = conector + 1 y nombre
`DualScreen`; así la app lo localiza (`MONITOR\DSC0001\…`) y Windows recuerda su posición entre conexiones.

## Compilar
Requiere Visual Studio 2022 con "Desarrollo de escritorio con C++" y el **Windows Driver Kit** (WDK) 10.0.22621+.
```powershell
msbuild virtual-display-driver\DualScreenIdd.sln /p:Configuration=Release /p:Platform=x64
```
Salida: `virtual-display-driver\build\x64\Release\DualScreenIdd\` (`.inf`, `.dll`, `.cat`).

> Estado: el código del controlador no se ha compilado ni probado todavía (se escribió sin WDK disponible).
> La primera compilación en CI o en un equipo con WDK puede requerir ajustes puntuales.
