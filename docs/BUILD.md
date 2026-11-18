# Compilar DualScreen desde cero

## 1. Requisitos

| Componente | Necesitas |
|---|---|
| App de Windows | Windows 10/11, [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Controlador | Visual Studio 2022 (C++), [WDK](https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk) 10.0.22621+ |
| App Android | JDK 17, Android SDK (plataforma 35, build-tools 35) o Android Studio |
| Señalización | Go 1.24+ (o Docker) |

## 2. Controlador
```powershell
msbuild virtual-display-driver\DualScreenIdd.sln /p:Configuration=Release /p:Platform=x64
.\scripts\sign-driver.ps1 -PackageDir virtual-display-driver\build\x64\Release\DualScreenIdd   # firma de prueba si no hay certificado
```

## 3. DualScreen.exe
```powershell
# Opcional: copiar el paquete del controlador para embeberlo (sin él, solo modo espejo)
New-Item -ItemType Directory -Force build\driver
Copy-Item virtual-display-driver\build\x64\Release\DualScreenIdd\* build\driver\

dotnet test windows\tests\DualScreen.Tests\DualScreen.Tests.csproj -c Release
dotnet publish windows\src\DualScreen\DualScreen.csproj -c Release -o out
```
Resultado: `out\DualScreen.exe` — un único archivo autocontenido (incluye el runtime de .NET; no requiere
instalar nada). Configura el repositorio de actualizaciones en `dualscreen.repo.json` antes de compilar.

> Desde Linux/macOS se puede comprobar el núcleo (`dotnet test`), pero la interfaz WPF requiere el SDK de .NET
> oficial de Microsoft con el componente de escritorio de Windows; lo más simple es compilar en Windows o en CI.

## 4. DualScreen.apk
```bash
cd android
./gradlew assembleDebug                        # app/build/outputs/apk/debug/app-debug.apk
./gradlew assembleRelease -PappVersion=0.1.0   # firmada si existe keystore.properties o variables ANDROID_*
```
`keystore.properties` (no se versiona):
```
storeFile=/ruta/release.jks
storePassword=…
keyAlias=dualscreen
keyPassword=…
```
Crear un keystore: `keytool -genkeypair -v -keystore release.jks -alias dualscreen -keyalg RSA -keysize 4096 -validity 10000`.

## 5. Servidor de señalización
```bash
cd signaling-server
go test ./...
go build -o dualscreen-signaling .
```

## 6. Probar
1. Ejecuta `DualScreen.exe`; instala el controlador si quieres modo extendido.
2. Instala la APK (`adb install -r app-debug.apk`).
3. Misma Wi‑Fi → en el teléfono, *Escanear QR*.
4. Registros: `%LOCALAPPDATA%\DualScreen\logs` y `adb logcat -s DualScreen.Hub DualScreen.Decoder DualScreen.Remote`.
