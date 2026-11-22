# Grounded 2 Minimap

Minimapa en overlay para **Grounded 2** (Steam). Se dibuja arriba a la derecha de la pantalla, encima del juego, con tu posición, hacia dónde mirás, tus amigos y lo mismo que muestra el mapa del juego (tus marcadores, camas, pings, misiones, mochila, mascota, coleccionables, lugares descubiertos), con los mismos íconos y colores.

- **Solo lectura**: lee la memoria del juego con `ReadProcessMemory`. No escribe, no inyecta DLLs y no modifica archivos del juego (los `.pak` solo se leen).
- **Liviano**: ~0,1 % de CPU, no usa la GPU del juego.
- **Aguanta actualizaciones**: no usa offsets ni listas fijas. Todo se encuentra por nombre con la reflexión de Unreal Engine y se lee de las tablas del propio juego: niveles, límites del mapa, tipos de marcador, íconos, colores, bichos. Si el juego agrega zonas, niveles, marcadores o bichos, aparecen solos.

## Uso

1. Poné el juego en **ventana sin bordes** (borderless). En pantalla completa exclusiva el overlay no se ve.
2. Ejecutá `dist\GroundedMinimap.exe`, antes o después de abrir el juego.
3. Aparece un ícono en la bandeja de Windows. Con **clic derecho** abrís el menú (configuración y salir) y con **doble clic** la configuración.

| Tecla | Acción |
|-------|--------|
| **F6** | Mostrar / ocultar el minimapa |
| **F7** | Pasar al siguiente nivel de zoom |
| **F8** | Norte fijo arriba o girar con la cámara |
| **F10** | Mapa grande (mantener apretada, o apretar para abrir y cerrar) |
| **F9** | Abrir la configuración |

Las teclas se pueden cambiar en la configuración. Solo responden cuando el juego está en primer plano.

### Configuración

Ventana con F9, doble clic en el ícono, o `GroundedMinimap.exe --settings`. Los cambios se ven en el momento (mientras está abierta, el minimapa queda visible como vista previa).

**Minimapa**: tamaño, zoom, opacidad, forma redonda o cuadrada, girar con la cámara, ocultar con Alt+Tab, movimiento suave, márgenes. **Mapa grande**: tamaño (% del alto de la ventana del juego), zoom, mantener apretada o alternar. **Teclas**.

**Qué mostrar** (cada cosa se activa o desactiva):

- Brújula (N, E, S, O).
- Rastro de por dónde pasaste en los últimos N segundos.
- Amigos (con el ícono de jugador del juego y su nombre; si están fuera de vista quedan en el borde señalando hacia dónde están).
- Marcadores del mapa del juego, por grupo: tus marcadores (con nombre y color), camas y reaparición, pings y recursos marcados, misiones, mochila u objetos tirados, mascota, coleccionables, alertas de la base, y "otros" (tipos que el juego agregue en el futuro). Los que el jugador ocultó en el mapa del juego tampoco aparecen.
- Lugares descubiertos (solo los que ya descubriste), con o sin nombre.
- Bichos cercanos, con o sin nombre, o solo hostiles. Forma y color según el carácter: triángulo rojo = hostil, rombo naranja = neutral, círculo verde = pacífico. Un bicho nuevo cuenta como hostil hasta que se agregue a `PassiveCreatures` / `NeutralCreatures`.

Todo se guarda en `dist\minimap.json`, que también se puede editar a mano. Ahí están además `ZoomLevels` (los niveles que recorre F7), las listas de bichos pacíficos/neutrales y unos límites de mapa de respaldo (`MinX/MaxX/MinY/MaxY`, solo se usan si no se pudieran leer del juego).

`dist\minimap.log` registra la conexión con el juego, las extracciones y lo que no se pudo leer. Si algo falla, mirá ahí primero. Se recorta solo cuando pasa de 256 KB.

## Requisitos

- Windows 10/11 x64
- **.NET 10 Desktop Runtime** para ejecutar el exe. Si se lo pasás a un amigo, tiene que instalarlo:
  `winget install Microsoft.DotNet.DesktopRuntime.10`
- Pasale la carpeta `dist` entera (con `extractor\`): sin el extractor el minimapa anda igual, pero con el mapa embebido del nivel principal y con íconos simples.

## Compilar

Requiere el **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`).

```powershell
.\build.ps1
```

Deja `dist\GroundedMinimap.exe` (~20 MB, lleva adentro el mapa principal para el primer arranque) y `dist\extractor\`.

Si ponés un `map.png` al lado del exe, se usa ese para el nivel principal en vez del extraído.

## Estructura

```
GroundedMinimap/
├── build.ps1                 Compila el overlay y el extractor -> dist/
├── dist/                     Ejecutable, extractor/, minimap.json, minimap.log
│   ├── maps/                 (caché) mapa de cada nivel extraído de los .pak
│   └── icons/                (caché) íconos del mapa del juego extraídos de los .pak
├── map/grounded2_map.png     Mapa principal 4096x4096 (se embebe en el exe como respaldo)
├── Overlay/                  Código del minimapa (WinForms, .NET 10)
│   ├── Program.cs            Arranque, configuración (minimap.json), log
│   ├── Overlay.cs            Ventana overlay, dibujo, teclas, mapa grande, ícono de bandeja
│   ├── SettingsForm.cs       Ventana de configuración
│   ├── GameReader.cs         Lectura del juego: jugador, amigos, nivel, marcadores, lugares, bichos
│   ├── MapStore.cs           Mapa por nivel (caché + extracción cuando cambian los .pak)
│   ├── IconStore.cs          Íconos del juego (caché + extracción)
│   ├── UE.cs                 Lector de Unreal Engine 5 (FNamePool, GUObjectArray, reflexión)
│   ├── app.ico               Ícono del exe, del tray y de la configuración
│   └── make_icon.ps1         Genera app.ico (powershell -File make_icon.ps1 -Out app.ico)
├── Extractor/                Extrae mapas e íconos de los .pak del juego (CUE4Parse)
└── Probe/                    Herramienta de diagnóstico: imprime offsets y posición en consola
```

## Cómo funciona

### Conexión (`UE.cs`)

1. Abre el proceso `Grounded2Steam-Win64-Shipping.exe` solo con permiso de lectura.
2. Recorre las secciones de datos del exe buscando dos estructuras globales de Unreal, sin firmas de bytes:
   - **FNamePool**: el primer bloque empieza con `"None"` y sigue con `"ByteProperty"`.
   - **GUObjectArray**: patrón `Objects*, PreAlloc*, Max, Num, MaxChunks, NumChunks`. `NumChunks` puede ser mayor que lo que necesita `Num` (el motor reserva chunks de más), y cada candidato se valida comprobando el índice de varios objetos antes de aceptarlo. El tamaño de cada ítem (24 bytes en Grounded 2) se detecta solo.
3. Con la reflexión del motor saca los offsets de cada propiedad por su nombre.

### Qué lee (`GameReader.cs`)

| Dato | De dónde |
|------|----------|
| Tu posición (~30/s) | `GameEngine → GameInstance → LocalPlayers[0] → PlayerController → Pawn → RootComponent → RelativeLocation`; enganchado (tirolesa) usa la cámara |
| Amigos | `World → GameState → PlayerArray → PlayerState.PawnPrivate` |
| Nivel y límites del mapa | `SurvivalGameInstance.LevelData` (el nivel cuyo `RelLevel`/`DevLevel` coincide con el mundo actual): `MinBounds`, `MaxBounds`, `MapTextureSize`, widget de fondo del mapa |
| Marcadores | `GameState.HUDMarkerManagerComponent`, todas sus listas `Registered*MarkerData`: posición, tipo (enum `EHUDMarkerType` leído por nombre), variante, color, nombre, ocultos |
| Íconos y colores | `GlobalUIData`: `HUDMarkerBrushes` (textura por tipo y variante), `LegendIconMap`, `PointOfInterestTypeMappings`, `HUDMarkerColorOptions` |
| Lugares descubiertos | Componentes `PointOfInterestComponent` del mundo, filtrados por `SurvivalPlayerState.DiscoveredPointsOfInterest` |
| Bichos | Todo actor que herede de `SurvivalCreature`, sin muertos (`HealthComponent.HealthState`) ni mascotas |

Lugares y bichos salen de un hilo aparte que recorre `GUObjectArray` cada 2 s; después de la primera pasada solo mira los objetos que cambiaron. Cada grupo es opcional: si un parche renombra algo, solo se apaga esa parte y queda anotado en el log.

### Mapas e íconos (`MapStore.cs`, `IconStore.cs`, `Extractor/`)

- La carpeta de `.pak` sale de la ruta del exe del juego (`<Proyecto>\Binaries\Win64` → `<Proyecto>\Content\Paks`).
- **Mapa**: el extractor abre el widget de fondo que indica `LevelData`, encuentra la textura que referencia y la decodifica. Se guarda en `dist\maps\<Nivel>.png` con una huella de los `.pak` (tamaño + fecha); si un parche los cambia se vuelve a extraer en segundo plano y mientras tanto se muestra el anterior.
- **Íconos**: el extractor recibe la lista de texturas que el juego usa en su mapa y las guarda en `dist\icons\`. Se tiñen con el color del marcador, como en el juego. Los que el juego dibuja con materiales (no extraíbles) usan el ícono de la leyenda equivalente, o una forma simple.
- No hay mappings (`.usmap`) públicos para esta versión, así que las texturas se leen de los bytes del paquete: el formato (`PF_BC7`, etc.) sale de la tabla de nombres y el único mip está al final del archivo. El tamaño sale de `LevelData` (mapa) o del tamaño del archivo (íconos cuadrados).

Extractor a mano:

```powershell
$paks = "F:\SteamLibrary\steamapps\common\Grounded2\Augusta\Content\Paks"
dist\extractor\Extractor.exe map   $paks Augusta /Game/UI/Map/UI_MapBGBackyard 4096 4096 mapa.png
dist\extractor\Extractor.exe icons $paks Augusta .\iconos /Game/UI/Images/MapIcons/T_UI_Flag_Map_Home
```

### Diagnóstico

```powershell
cd Probe
dotnet run -c Release
```

Muestra en consola las estructuras encontradas, los offsets resueltos, el pawn local y su posición.

## Notas

- Probado con la build `Augusta-CL-3235807` de Steam.
- Grounded 2 no trae anti-cheat. Igual, es una herramienta externa no oficial: usala bajo tu responsabilidad.
