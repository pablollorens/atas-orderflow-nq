# Primera corrida en Market Replay — Checklist

Guía concisa para validar `OrderFlowNQ_v2_APlus` con datos reales de footprint.
ATAS **no** tiene backtester de ticks: Market Replay es el único camino válido.

## 0. Compilar y desplegar
- [x] `dotnet build -c Release` → debe terminar en **0 warnings / 0 errors**.
- [x] El target `DeployToAtas` copia el DLL a `%APPDATA%\ATAS\Strategies`.
      (Verás en el log: `Copiado OrderFlowNQ.dll a ...\ATAS\Strategies`.)
- [x] Si ATAS estaba abierto, ciérralo antes de compilar (bloquea el DLL).

## 1. Arrancar ATAS
- [x] Inicia sesión con tu usuario/contraseña (Replay descarga datos de la nube).
- [x] *Chart Strategies* → botón **refrescar** la lista de estrategias.
- [x] Confirma que aparece **`OrderFlowNQ_v2_APlus`**.

## 2. Configurar Market Replay
- [x] Activa **Market Replay**.
- [x] Modo de datos:
      - *Ticks + Generated DOM* → hasta 1 semana (rápido para iterar).
      - *Ticks + DOM* → 1 día, máxima precisión.
- [x] Elige un **día concreto de alta actividad** (no toda una semana en la 1ª vez).
- [ ] Abre una **Replay Account** (el `Portfolio` donde se ejecutan las órdenes).
      ⚠️ Sin ella, `OpenOrder` no va a ningún sitio.

## 3. Configurar el chart
- [ ] Abre un chart **footprint / cluster** del instrumento (p. ej. **MNQ**).
- [ ] Añade la estrategia desde *Chart Strategies*.
- [ ] Ajusta `DollarsPerPoint` al instrumento: **MNQ=2, MES=5, NQ=20**.
- [ ] Deja **`DebugMode = true`**.
- [ ] (Opcional) `UseSessionFilter = false` en la 1ª corrida, para no filtrar señales.

## 4. Ejecutar y leer el log

> ⚠️ **ORDEN CRÍTICO (si no, no opera):**
> 1. **Primero pulsa PLAY** en el Market Replay y deja que empiece a reproducir.
> 2. **Después activa la estrategia** (el toggle de *Chart Strategies*).
>
> Si la activas antes de darle a Play, ATAS recarga la historia y la apaga
> (*"Strategy stopped on history data reloaded"*). Además, la estrategia **solo
> opera en tiempo real**: verás `[OFNQ] Tiempo real activo` cuando empiece a
> operar; antes de esa línea no entra nada (es correcto). Y el volcado
> `[OFNQ][DAY ...]` sale al **cambiar de día**, así que deja correr el replay.

- [ ] Abre la ventana **Logs** y sigue la secuencia de una operación:
      `ENTRY → FILL → BRACKET → (Register Stop _SL + Limit _TP) → EXIT`.

Diagnóstico según contadores `[OFNQ][DAY ...]`:
- [ ] `signals > 0` y trades en el chart → **la cadena funciona** ✅
- [ ] `siBull/siBear > 0` pero `signals = 0` → falta absorción o confirmación:
      activa **`Modo más entradas`** para validar, o baja `AbsorptionVolMin`.
- [ ] **Todo en 0** (incluido `bars`) → problema de footprint o la estrategia
      no recibe velas (revisa instrumento/cuenta Replay).
- [ ] Entradas/salidas: `[OFNQ] ENTRY ...` / `[OFNQ] EXIT ...`.
- [ ] `ORDER FAILED: ...` → revisa Portfolio/Replay Account.

> 💡 Por defecto va el **A+ estricto** (pocas entradas, alta calidad). El toggle
> **`Modo más entradas`** relaja los filtros para ver muchas más entradas (peor
> calidad) — útil para validar que la cadena entra/sale bien.

## 5. Revisar resultados
- [ ] Abre el **Trading Journal** (Account = Replay): profit factor, drawdown, R.
- [ ] Solo hay **una** cuenta Replay y cada sesión **resetea** la anterior →
      **exporta** lo que quieras conservar antes de la siguiente corrida.

## 6. Iterar
- [ ] Empieza por días sueltos de alta actividad (el Replay corre en tiempo real).
- [ ] Mueve primero los parámetros del grupo **Signal**:
      `ZonePct`, `ImbalanceRatio`, `MinStackedLevels`, `AbsorptionVolMin`.
- [ ] Cuando la señal sea estable, ajusta el grupo **Risk**.

---

### Pendientes conocidos (roadmap)
- [ ] Confirmar la zona horaria de `candle.Time` para `UseSessionFilter`
      (puede venir en UTC; ajusta `SessionStartHour`/`SessionEndHour` a la tz del exchange).
- [ ] Validar la gestión de posición contra fills reales (vs el stop sintético por cierre).
- [ ] Portar setups 2-4 (retest, falsa ruptura, continuación) desde `reference/`.
