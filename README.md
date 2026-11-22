# Grounded 2 Minimap

Minimapa en overlay para **Grounded 2** (Steam). Se dibuja arriba a la derecha de la pantalla, encima del juego, con tu posición, hacia dónde mirás y dónde están tus amigos.

- **Solo lectura**: lee la memoria del juego con `ReadProcessMemory`. No escribe, no inyecta DLLs y no toca archivos del juego.
- **Liviano**: ~0,1 % de CPU, no usa la GPU del juego.
- **Aguanta actualizaciones**: no usa offsets fijos, encuentra todo por nombre usando la reflexión de Unreal Engine.

## Uso

1. Poné el juego en **ventana sin bordes** (borderless). En pantalla completa exclusiva el overlay no se ve.
2. Ejecutá `dist\GroundedMinimap.exe`, antes o después de abrir el juego.
3. Aparece un ícono en la bandeja de Windows. Con **clic derecho** abrís el menú (configuración y salir) y con **doble clic** la configuración.

| Tecla | Acción |
|-------|--------|
| **F6** | Mostrar / ocultar el minimapa |
| **F7** | Pasar al siguiente nivel de zoom |
| **F8** | Norte fijo arriba o girar con la cámara |
| **F9** | Abrir la configuración |

Las teclas se pueden cambiar en la configuración. Solo responden cuando el juego está en primer plano.

### Configuración

Ventana con F9, doble clic en el ícono, o `GroundedMinimap.exe --settings`. Los cambios se ven en el momento:

- Tamaño (120–700 px)
- Zoom (deslizador continuo, de cerca a lejos)
- Opacidad
- Forma redonda o cuadrada
- Girar con la cámara
- Ocultar cuando hacés Alt+Tab
- Márgenes desde la esquina superior derecha
- Teclas

Todo se guarda en `dist\minimap.json`, que también se puede editar a mano. Ahí están además `ZoomLevels` (los niveles que recorre F7) y los límites del mapa (`MinX/MaxX/MinY/MaxY`).

`dist\minimap.log` registra el estado de la conexión con el juego. Si algo falla, mirá ahí primero.

### Amigos

En cooperativo, los demás jugadores aparecen como puntos celestes con su nombre. Si están fuera del minimapa, el punto queda en el borde señalando hacia dónde están.

## Requisitos

- Windows 10/11 x64
- **.NET 10 Desktop Runtime** para ejecutar el exe. Si se lo pasás a un amigo, tiene que instalarlo:
  `winget install Microsoft.DotNet.DesktopRuntime.10`

## Compilar

Requiere el **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`).

```powershell
.\build.ps1
```

Deja el ejecutable en `dist\GroundedMinimap.exe`. Es un solo archivo de ~20 MB porque lleva el mapa adentro.

Si ponés un `map.png` al lado del exe, se usa ese en vez del que viene adentro.

## Estructura

```
GroundedMinimap/
├── build.ps1                 Compila el overlay -> dist/
├── dist/                     Ejecutable + minimap.json + minimap.log
├── map/grounded2_map.png     Mapa 4096x4096 extraído del juego (se embebe en el exe)
├── Overlay/                  Código del minimapa (WinForms, .NET 10)
│   ├── Program.cs            Config, ventana overlay, dibujo, teclas, ícono de bandeja
│   ├── SettingsForm.cs       Ventana de configuración
│   ├── GameReader.cs         Hilo que lee la posición del jugador y de los amigos
│   └── UE.cs                 Lector de Unreal Engine 5 (FNamePool, GUObjectArray, reflexión)
├── Extractor/                Herramienta para extraer el mapa de los .pak del juego (CUE4Parse)
└── Probe/                    Herramienta de diagnóstico: imprime offsets y posición en consola
```

## Cómo funciona

### Posición del jugador (`UE.cs`, `GameReader.cs`)

1. Abre el proceso `Grounded2Steam-Win64-Shipping.exe` solo con permiso de lectura.
2. Recorre las secciones de datos del exe buscando dos estructuras globales de Unreal, sin usar firmas de bytes:
   - **FNamePool**: el primer bloque empieza con `"None"` y sigue con `"ByteProperty"`.
   - **GUObjectArray**: patrón `Objects*, PreAlloc*, Max, Num, MaxChunks, NumChunks` donde los chunks cuadran con Max y Num. En Grounded 2 cada ítem mide 24 bytes y el puntero al objeto está en +8; esto se detecta solo.
3. Con la reflexión del motor saca los offsets de propiedades por nombre (`GameEngine.GameInstance`, `PlayerController.AcknowledgedPawn`, `Actor.RootComponent`, `SceneComponent.RelativeLocation`, `Controller.ControlRotation`, etc.).
4. Sigue la cadena `GameEngine → GameInstance → LocalPlayers[0] → PlayerController → Pawn → RootComponent → RelativeLocation` unas 30 veces por segundo. Si el pawn está enganchado a otra cosa (por ejemplo una tirolesa), usa la posición de la cámara.
5. Los amigos salen de `GameViewport → World → GameState → PlayerArray → PlayerState.PawnPrivate`.

### Mapa

- **Imagen**: `Augusta/Content/_Augusta/UI/Images/Map/T_UI_AugustaMainMap`, una textura BC7 de 4096×4096. No hay un archivo de mappings (`.usmap`) público para esta versión, así que la textura se lee directo de los bytes del asset: los datos empiezan en el byte 502 del `.uasset`.
- **Calibración**: los límites salen del struct `LevelData` del juego (`MinBounds` / `MaxBounds`):
  - X: −261500 … 73500, de izquierda a derecha
  - Y: −137500 … 197500, de arriba a abajo
  - `pixel = (pos - min) / (max - min) * 4096`

### Volver a extraer el mapa (si un parche lo cambia)

```powershell
cd Extractor
dotnet build -c Release -o bin\out
# Volcar el asset crudo
bin\out\Extractor.exe raw "Augusta/Content/_Augusta/UI/Images/Map/T_UI_AugustaMainMap.uasset" GAME_UE5_6 ..\map\mainmap.raw
# Decodificar BC7 -> PNG (offset 502; si cambia, buscar el primer bloque BC7 después del string "PF_BC7")
bin\out\Extractor.exe bc7 ..\map\mainmap.raw 502 ..\map\grounded2_map.png 4096 4096
```

La ruta a los `.pak` del juego está fija en `Extractor/Program.cs` (`F:\SteamLibrary\...`). Cambiala si el juego está en otro lugar.

### Diagnóstico

```powershell
cd Probe
dotnet run -c Release
```

Muestra en consola las estructuras encontradas, los offsets resueltos, el pawn local y su posición.

## Notas

- Probado con la build `Augusta-CL-3235807` de Steam.
- Grounded 2 no trae anti-cheat. Igual, es una herramienta externa no oficial: usala bajo tu responsabilidad.
