# atas-orderflow-nq

Estrategia algorítmica de **order flow** para **ATAS v8** (C#, `ChartStrategy`),
basada en el gatillo **A+**: *Absorción → Stacked Imbalance en zona extrema →
Confirmación de precio*. Pensada para futuros índice intradía (MNQ / MES / NQ).

---

## Tabla de contenidos
- [Qué es](#qué-es)
- [La estrategia: Setup A+](#la-estrategia-setup-a)
- [Por qué la versión original no generaba órdenes](#por-qué-la-versión-original-no-generaba-órdenes)
- [Estructura del repositorio](#estructura-del-repositorio)
- [Requisitos](#requisitos)
- [Compilar (Windows + Zed)](#compilar-windows--zed)
- [Desplegar en ATAS](#desplegar-en-atas)
- [Testear en Market Replay](#testear-en-market-replay)
- [Parámetros](#parámetros)
- [Pendiente / roadmap](#pendiente--roadmap)
- [Créditos](#créditos)

---

## Qué es

ATAS permite escribir estrategias en C# heredando de `ChartStrategy`. El método
`OnCalculate(int bar, decimal value)` se ejecuta en cada barra histórica y luego
en cada tick de la barra en curso; dentro decides y envías órdenes con `OpenOrder`.

Este repo contiene una estrategia que automatiza el patrón de order flow A+ y deja
toda la lógica parametrizada para poder optimizarla después.

## La estrategia: Setup A+

El gatillo, sobre tres velas:

```
Vela N-2:  Aparece ABSORCIÓN (un participante grande absorbe el lado contrario)
Vela N-1:  STACKED IMBALANCE en zona extrema de la vela
             · LONG  -> imbalance comprador en el 30% inferior
             · SHORT -> imbalance vendedor en el 30% superior
           (la vela no hace nuevo extremo contra N-2)
Vela N:    CONFIRMACIÓN de precio
             · LONG  -> cierra por encima del máximo de N-1
             · SHORT -> cierra por debajo del mínimo de N-1
           -> ENTRADA al cierre de N
```

**Gestión:** stop a `StopPoints`, take profit a `StopPoints × TargetRR`, break-even
a `BeTriggerR` (mueve el stop a entrada + `BeOffsetPoints`), cierre forzado a
`ForceCloseR`. Cortacircuitos diarios por nº de trades y por pérdida en USD.

La regla de la zona del 30% se calcula así:

```
rango        = High - Low
posRelativa  = (precioImbalance - Low) / rango
posRelativa <= 0.30  -> zona inferior (señal alcista)
posRelativa >= 0.70  -> zona superior (señal bajista)
```

## Por qué la versión original no generaba órdenes

La versión inicial instanciaba los indicadores `StackedImbalance` / `Absorption`,
hacía `Add()` y leía sus valores con `DataSeries[0..3]` casteado a `ValueDataSeries`.

El problema: esos indicadores de order flow son **de renderizado** — su lógica vive
en `OnRender` y no exponen la señal en una serie numérica. La lectura devolvía 0,
todas las señales quedaban en `false` y nunca se evaluaba ningún setup -> **cero
órdenes**. Para colmo, un `catch { return 0; }` se tragaba cualquier excepción, así
que ni siquiera había error visible. Faltaba además implementar `OnOrderRegisterFailed`,
con lo que cualquier orden rechazada por el exchange pasaba desapercibida.

**Solución (versión v2):** las señales se calculan **directamente del footprint de
la vela** (`GetAllPriceLevels`, `MaxVolumePriceInfo`, volumen bid/ask por nivel de
precio) en vez de leer otros indicadores. Además:

- Se implementa la regla del 30%.
- Se actualiza el PnL diario en cada cierre (antes nunca se sumaba).
- Se implementa `OnOrderRegisterFailed` (los fallos ahora se loguean).
- `DebugMode` vuelca contadores diarios para ver dónde se rompe la cadena.

> ⚠️ **Verificación pendiente.** Las llamadas al footprint marcadas con `// VERIFY`
> (`GetAllPriceLevels`, `GetPriceVolumeInfo`, `MaxVolumePriceInfo`, y las propiedades
> `.Bid / .Ask / .Volume / .Price` de `PriceVolumeInfo`) pueden cambiar de nombre
> entre versiones de la API. Con `DebugMode = true`, la primera corrida en Replay
> te confirma si los valores llegan (si los contadores se mueven, los nombres son correctos).

## Estructura del repositorio

```
atas-orderflow-nq/
├── OrderFlowNQ.csproj          Proyecto .NET 8, referencias a las DLL de ATAS
├── README.md
├── .gitignore                  Excluye bin/obj y las DLL de la plataforma
├── src/
│   └── OrderFlowNQ_v2_APlus.cs  Versión activa — la única que se compila
└── reference/                  Código original (NO se compila, solo lectura)
    ├── OrderFlowNQ_v1_Jun9.cs   Versión simplificada
    └── OrderFlowNQ_Jun8_full.cs Versión completa (4 setups) — cantera de ideas
```

Las dos versiones de `reference/` declaran la misma clase `OrderFlowNQ`, por eso se
excluyen del build (`<Compile Remove="reference/**/*.cs" />`). Quedan como histórico
y como fuente de los setups extra (retest, falsa ruptura, continuación).

## Requisitos

- **Windows** (ATAS es solo-Windows; no uses WSL para esto).
- **ATAS v8** instalado y con sesión iniciada.
- **.NET 8 SDK** — https://dotnet.microsoft.com/download
- Editor: **Zed** (o Visual Studio / Rider si quieres depurar paso a paso).

## Compilar (Windows + Zed)

Zed edita y da autocompletado de C# por LSP, pero no compila .NET por sí mismo:
el build se hace desde la terminal.

1. Abre la carpeta del repo en Zed.
2. **Ajusta `<AtasPath>`** en `OrderFlowNQ.csproj` a la carpeta donde esté
   `ATAS.Strategies.dll` (busca ese fichero en tu instalación; suele estar en
   `%LOCALAPPDATA%\ATAS Platform\current`). Es el único cambio obligatorio.
   Si tu ATAS fuese antiguo (.NET Framework), cambia `net8.0-windows` por `net472`.
3. En la terminal integrada de Zed:

   ```powershell
   dotnet build -c Release
   ```

   Genera `bin/Release/OrderFlowNQ.dll`.

## Desplegar en ATAS

1. Copia `bin/Release/OrderFlowNQ.dll` a
   `C:\Users\<TU_USUARIO>\Documents\ATAS\Strategies`
   (o descomenta el `Target DeployToAtas` del `.csproj` para que lo copie solo al compilar).
2. En ATAS, pulsa el botón de **refresco** de la lista de estrategias.
3. La estrategia aparece como **`OrderFlowNQ_v2_APlus`**.

## Testear en Market Replay

ATAS **no tiene backtester histórico de ticks**: la simulación realista se hace en
**Market Replay** (datos de tick + DOM desde la nube). Para una estrategia de order
flow es el único camino válido, porque las señales necesitan footprint real.

1. Inicia sesión en ATAS con tu usuario/contraseña.
2. Activa **Market Replay** -> modo *Ticks + Generated DOM* (hasta 1 semana) o
   *Ticks + DOM* (1 día, máxima precisión). Fija las fechas y pulsa Play.
3. Abre una **Replay Account** — es el `Portfolio` donde se ejecutan las órdenes
   simuladas. Sin ella, `OpenOrder` no va a ningún sitio.
4. Abre un chart **footprint / cluster** del instrumento (p. ej. MNQ).
5. Añade la estrategia desde *Chart Strategies*, con `DebugMode = true`.
6. Pulsa Play y abre la ventana **Logs**. Lee el volcado diario `[OFNQ][DAY ...]`:
   - `signals > 0` y trades en el chart -> la cadena funciona.
   - `absBull/absBear` se mueven pero `siBull/siBear = 0` -> ajusta `ImbalanceRatio` / `MinStackedLevels`.
   - todo en 0 -> revisa los nombres `// VERIFY` del footprint.
7. Revisa el **Trading Journal** (Account = Replay): profit factor, drawdown, etc.
   Solo hay una cuenta Replay y cada sesión resetea la anterior -> exporta lo que quieras conservar.

> El Replay corre en tiempo real (no hay salto de sesión/vela), así que iterar semanas
> es lento. Empieza por días concretos de alta actividad y sube la velocidad de playback.

## Parámetros

**Signal** (los que más moverás al optimizar)

| Parámetro | Default | Qué hace |
|---|---|---|
| `ZonePct` | 0.30 | Zona extrema de la vela para validar el SI |
| `ImbalanceRatio` | 3.0 | Ratio ask/bid diagonal para contar imbalance |
| `MinStackedLevels` | 3 | Nº de niveles consecutivos en imbalance |
| `AbsorptionVolMin` | 200 | Volumen mínimo en el nivel clave para absorción |
| `AbsorptionLookback` | 3 | Velas que sigue vigente una absorción |
| `RequireAbsorption` | true | Exigir absorción previa para el A+ |
| `EnableSiDouble` | false | Activar el setup secundario (doble SI) |

**Risk**

| Parámetro | Default | Qué hace |
|---|---|---|
| `Quantity` | 1 | Contratos por entrada |
| `StopPoints` | 3.0 | Stop en puntos |
| `TargetRR` | 2.0 | Ratio riesgo/beneficio del TP |
| `BeTriggerR` | 1.0 | R a la que se activa break-even |
| `BeOffsetPoints` | 0.1 | Offset del stop al pasar a BE |
| `ForceCloseR` | 3.0 | Cierre forzado a esta R |
| `DollarsPerPoint` | 2.0 | MNQ=2, MES=5, NQ=20 |
| `MaxTrades` | 20 | Máx operaciones/día |
| `MaxDailyLoss` | 5000 | Pérdida máx diaria (USD) |

**Session**: `UseSessionFilter`, `SessionStartHour`, `SessionEndHour`, `DebugMode`.

## Pendiente / roadmap

- [ ] Cerrar los `// VERIFY` del footprint contra la versión exacta de ATAS.
- [ ] Portar los setups 2-4 (retest, falsa ruptura, continuación) desde la versión completa.
- [ ] Confirmar la zona horaria de `candle.Time` para el filtro de sesión.
- [ ] Validar la gestión de posición contra fills reales (vs el stop sintético por cierre).

## Créditos

Especificación y código base originales de Juan (`juansanca1992`). Reescritura de la
capa de señal, parametrización y herramientas de diagnóstico en la versión v2.
