# Primera corrida en Market Replay — Checklist

Guía concisa para validar `OrderFlowNQ_v2_APlus` con datos reales de footprint.
ATAS **no** tiene backtester de ticks: Market Replay es el único camino válido.

## 0. Compilar y desplegar
- [ ] `dotnet build -c Release` → debe terminar en **0 warnings / 0 errors**.
- [ ] El target `DeployToAtas` copia el DLL a `%APPDATA%\ATAS\Strategies`.
      (Verás en el log: `Copiado OrderFlowNQ.dll a ...\ATAS\Strategies`.)
- [ ] Si ATAS estaba abierto, ciérralo antes de compilar (bloquea el DLL).

## 1. Arrancar ATAS
- [ ] Inicia sesión con tu usuario/contraseña (Replay descarga datos de la nube).
- [ ] *Chart Strategies* → botón **refrescar** la lista de estrategias.
- [ ] Confirma que aparece **`OrderFlowNQ_v2_APlus`**.

## 2. Configurar Market Replay
- [ ] Activa **Market Replay**.
- [ ] Modo de datos:
      - *Ticks + Generated DOM* → hasta 1 semana (rápido para iterar).
      - *Ticks + DOM* → 1 día, máxima precisión.
- [ ] Elige un **día concreto de alta actividad** (no toda una semana en la 1ª vez).
- [ ] Abre una **Replay Account** (el `Portfolio` donde se ejecutan las órdenes).
      ⚠️ Sin ella, `OpenOrder` no va a ningún sitio.

## 3. Configurar el chart
- [ ] Abre un chart **footprint / cluster** del instrumento (p. ej. **MNQ**).
- [ ] Añade la estrategia desde *Chart Strategies*.
- [ ] Ajusta `DollarsPerPoint` al instrumento: **MNQ=2, MES=5, NQ=20**.
- [ ] Deja **`DebugMode = true`**.
- [ ] (Opcional) `UseSessionFilter = false` en la 1ª corrida, para no filtrar señales.

## 4. Ejecutar y leer el log
- [ ] Pulsa **Play**. Sube la velocidad de playback.
- [ ] Abre la ventana **Logs** y busca el volcado diario `[OFNQ][DAY ...]`.

Diagnóstico según contadores:
- [ ] `signals > 0` y trades en el chart → **la cadena funciona** ✅
- [ ] `absBull/absBear` se mueven pero `siBull/siBear = 0` →
      ajusta `ImbalanceRatio` (baja) y/o `MinStackedLevels` (baja).
- [ ] `siBull/siBear > 0` pero `signals = 0` →
      revisa `RequireAbsorption` / `AbsorptionLookback` / confirmación de precio.
- [ ] **Todo en 0** (incluido `bars`) → problema de acceso al footprint o la
      estrategia no recibe velas (revisa instrumento/cuenta Replay).
- [ ] Entradas: línea `[OFNQ] ENTRY ...`; cierres: `[OFNQ] CLOSE ...`.
- [ ] Fallos de orden: `[OFNQ] ORDER FAILED: ...` (revisa Portfolio/Replay Account).

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
