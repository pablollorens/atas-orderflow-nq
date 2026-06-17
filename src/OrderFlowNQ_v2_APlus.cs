using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Strategies.Chart;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Utils.Common.Logging;

namespace OrderFlowNQ
{
    /// <summary>
    /// OrderFlowNQ_v2 — Setup A+ (Absorción → Stacked Imbalance en zona extrema → Confirmación de precio)
    ///
    /// CAMBIO CLAVE frente a la versión de Juan:
    ///   Las señales de order flow se calculan DIRECTAMENTE del footprint de la vela
    ///   (GetCandle(bar) + niveles de precio bid/ask), NO leyendo DataSeries de otros
    ///   indicadores. Esa lectura era la razón de que no se generara ninguna orden:
    ///   StackedImbalance / Absorption no exponen su valor en una ValueDataSeries.
    ///
    /// NOTA DE VERIFICACIÓN: los nombres exactos de las propiedades de PriceVolumeInfo
    /// (Bid / Ask / Volume / Price) pueden variar entre versiones de la API de ATAS.
    /// Activa DebugMode y mira el log la primera vez para confirmar que los valores
    /// llegan. Todo lo marcado con // VERIFY conviene comprobarlo contra tu ATAS v8.
    /// </summary>
    [DisplayName("OrderFlowNQ_v2_APlus")]
    public class OrderFlowNQ_v2 : ChartStrategy
    {
        // ════════════════ PARÁMETROS — RIESGO / GESTIÓN ════════════════
        [Display(Name = "Quantity (contracts)", GroupName = "Risk", Order = 1)]
        public int Quantity { get; set; } = 1;

        [Display(Name = "Stop points", GroupName = "Risk", Order = 2)]
        public decimal StopPoints { get; set; } = 10.0m;        // NQ M5: 3 pts queda dentro del ruido; 10 pts es más realista

        [Display(Name = "Target RR", GroupName = "Risk", Order = 3)]
        public decimal TargetRR { get; set; } = 2.0m;

        [Display(Name = "Break-even trigger (R)", GroupName = "Risk", Order = 4)]
        public decimal BeTriggerR { get; set; } = 1.0m;

        [Display(Name = "Break-even offset (points)", GroupName = "Risk", Order = 5)]
        public decimal BeOffsetPoints { get; set; } = 1.0m;     // 0.1 era sub-tick (NQ tick=0.25); 1 pt cubre slippage/comisión

        [Display(Name = "Force close at (R)", GroupName = "Risk", Order = 6)]
        public decimal ForceCloseR { get; set; } = 3.0m;

        [Display(Name = "$ per point per contract", GroupName = "Risk", Order = 7)]
        public decimal DollarsPerPoint { get; set; } = 20.0m; // NQ = 20.0, MNQ = 2.0, MES = 5.0

        [Display(Name = "Max trades / day", GroupName = "Risk", Order = 8)]
        public int MaxTrades { get; set; } = 20;

        [Display(Name = "Max daily loss USD", GroupName = "Risk", Order = 9)]
        public decimal MaxDailyLoss { get; set; } = 5000m;

        // Cortacircuitos: tras N pérdidas seguidas en el día, deja de entrar (evita la
        // sangría de stops en serie cuando el mercado tendencia en contra). 0 = desactivado.
        [Display(Name = "Max consecutive losses (0=off)", GroupName = "Risk", Order = 10)]
        public int MaxConsecutiveLosses { get; set; } = 3;

        // ════════════════ PARÁMETROS — SEÑAL (para optimizar) ════════════════
        [Display(Name = "SI extreme zone %", GroupName = "Signal", Order = 20)]
        public decimal ZonePct { get; set; } = 0.30m;            // 30% inferior/superior

        [Display(Name = "Imbalance ratio", GroupName = "Signal", Order = 21)]
        public decimal ImbalanceRatio { get; set; } = 3.0m;      // ask vs bid diagonal

        [Display(Name = "Min stacked levels", GroupName = "Signal", Order = 22)]
        public int MinStackedLevels { get; set; } = 3;           // nº de niveles consecutivos

        [Display(Name = "Absorption volume min", GroupName = "Signal", Order = 23)]
        public decimal AbsorptionVolMin { get; set; } = 100m;    // volumen en el nivel clave (era 200: con NQ Replay nunca saltaba; súbelo si entra demasiado)

        [Display(Name = "Absorption lookback (bars)", GroupName = "Signal", Order = 24)]
        public int AbsorptionLookback { get; set; } = 3;         // vigencia de la absorción

        [Display(Name = "Require absorption", GroupName = "Signal", Order = 25)]
        public bool RequireAbsorption { get; set; } = true;

        [Display(Name = "Enable SI-double setup", GroupName = "Signal", Order = 26)]
        public bool EnableSiDouble { get; set; } = false;

        // Interruptor "MÁS ENTRADAS": relaja los 4 cuellos de botella a la vez (sin recompilar):
        // no exige absorción, ensancha la zona, baja el ratio de imbalance y pide menos
        // niveles apilados. Por defecto OFF → el setup A+ estricto (la estrategia real).
        // Actívalo solo para validar la cadena o explorar más entradas (peor calidad).
        [Display(Name = "Modo más entradas (laxo)", GroupName = "Signal", Order = 27)]
        public bool LooseMode { get; set; } = false;

        // ════════════════ PARÁMETROS — SESIÓN / DEBUG ════════════════
        [Display(Name = "Restrict to session hours", GroupName = "Session", Order = 40)]
        public bool UseSessionFilter { get; set; } = false;

        [Display(Name = "Session start hour (exchange tz)", GroupName = "Session", Order = 41)]
        public int SessionStartHour { get; set; } = 9;

        [Display(Name = "Session end hour (exchange tz)", GroupName = "Session", Order = 42)]
        public int SessionEndHour { get; set; } = 16;

        [Display(Name = "Debug log", GroupName = "Session", Order = 43)]
        public bool DebugMode { get; set; } = true;

        // ════════════════ ESTADO INTERNO ════════════════
        private int _tradesToday;
        private decimal _pnlTodayUsd;
        private int _consecutiveLosses;
        private DateTime _lastDate = DateTime.MinValue;
        private int _lastBar = -1;

        private decimal _entryPrice;
        private decimal _initialRisk;
        private int _posDir;               // dirección de la operación en curso (+1/-1), 0 = plano
        private bool _beActivated;

        // Ejecución live-grade dirigida por FILLS (OnNewMyTrade); el estado de posición
        // lo lleva _posDir, no CurrentPosition (poco fiable en Replay).
        private Order? _slOrder;           // Stop real (se mueve a BE con ModifyOrder)
        private Order? _tpOrder;           // Limit real (take profit)
        private bool _entryPending;        // entrada a mercado enviada, esperando confirmación de fill
        private string _pendingLabel = "";
        private int _ocoCounter;           // contador para grupos OCO únicos

        private bool _historyDone;         // true cuando terminó el recálculo histórico → tiempo real

        private int _absBullBar = -1;
        private int _absBearBar = -1;

        // contadores de diagnóstico (se vuelcan al log al cerrar sesión)
        private int _dbgBars, _dbgAbsBull, _dbgAbsBear, _dbgSiBull, _dbgSiBear, _dbgSignals;

        public OrderFlowNQ_v2()
        {
            // La estrategia no dibuja nada: no necesitamos DataSeries propias.
        }

        // ════════════════ UMBRALES EFECTIVOS (según LooseMode) ════════════════
        // En modo laxo se relajan los umbrales; si el usuario ya los puso más
        // laxos a mano, se respeta el valor más permisivo de los dos.
        private bool EffRequireAbsorption => RequireAbsorption && !LooseMode;
        private int EffMinStacked => LooseMode ? Math.Min(MinStackedLevels, 2) : MinStackedLevels;
        private decimal EffImbalanceRatio => LooseMode ? Math.Min(ImbalanceRatio, 2.0m) : ImbalanceRatio;
        private decimal EffZonePct => LooseMode ? Math.Max(ZonePct, 0.45m) : ZonePct;

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == _lastBar) return;
            if (bar < 5) return;
            if (!CanProcess(bar)) return;          // gate nativo de ATAS (ChartStrategy.CanProcess)
            _lastBar = bar;

            // No operar durante el recálculo histórico del arranque: ATAS no ejecuta
            // órdenes en esa fase (el market quedaba en "state None", sin rellenar, sin
            // posición ni bracket). Solo operamos en tiempo real (replay en play).
            if (!_historyDone) return;

            var candle = GetCandle(bar);
            var today = candle.Time.Date;

            if (today != _lastDate)
            {
                if (DebugMode && _lastDate != DateTime.MinValue) DumpDailyDebug();
                _tradesToday = 0;
                _pnlTodayUsd = 0m;
                _consecutiveLosses = 0;
                _lastDate = today;
                _absBullBar = -1;
                _absBearBar = -1;
                _dbgBars = _dbgAbsBull = _dbgAbsBear = _dbgSiBull = _dbgSiBear = _dbgSignals = 0;
            }

            _dbgBars++;

            // Si hay una operación en curso no buscamos otra. La entrada y la salida se
            // manejan por FILLS reales (OnNewMyTrade), NO por CurrentPosition: en este
            // Replay CurrentPosition lee 0 aunque haya posición y OnCurrentPositionChanged
            // no se dispara. Aquí solo gestionamos el break-even del stop real.
            if (_posDir != 0) { ManagePosition(candle); return; }

            // Cortacircuitos diarios
            if (_tradesToday >= MaxTrades) return;
            if (_pnlTodayUsd <= -MaxDailyLoss) return;
            if (MaxConsecutiveLosses > 0 && _consecutiveLosses >= MaxConsecutiveLosses) return;

            if (UseSessionFilter && !InSession(candle)) return;

            // ── Señales sobre velas YA CERRADAS ──
            // Procesamos en el primer tick de cada vela nueva, así que bar-1 acaba
            // de cerrar y su Close ya es definitivo. La confirmación DEBE leerse de
            // una vela cerrada (antes se leía de la vela en formación → Close≈Open,
            // nunca confirmaba → signals=0). Mapea al A+ de Juan:
            //   conf = bar-1 → vela de confirmación (N)
            //   sig  = bar-2 → vela del stacked imbalance (N-1)
            int conf = bar - 1;
            int sig = bar - 2;
            decimal entry = candle.Close;            // precio actual ≈ fill real del market order (no el cierre stale de conf)

            // Registrar absorción sobre la vela recién cerrada (recencia vía AbsorptionLookback)
            if (AbsorptionBull(conf)) { _absBullBar = conf; _dbgAbsBull++; }
            if (AbsorptionBear(conf)) { _absBearBar = conf; _dbgAbsBear++; }

            bool siBull = SiBullZone(sig); if (siBull) _dbgSiBull++;
            bool siBear = SiBearZone(sig); if (siBear) _dbgSiBear++;

            // ── SETUP A+ LONG: absorción reciente + SI verde en zona baja + confirmación ──
            bool absBullOk = !EffRequireAbsorption || (_absBullBar >= 0 && conf - _absBullBar <= AbsorptionLookback);
            if (absBullOk && siBull && BullConfirm(conf))
            {
                _dbgSignals++;
                ExecuteEntry(+1, "APLUS_L", entry);
                return;
            }

            // ── SETUP A+ SHORT ──
            bool absBearOk = !EffRequireAbsorption || (_absBearBar >= 0 && conf - _absBearBar <= AbsorptionLookback);
            if (absBearOk && siBear && BearConfirm(conf))
            {
                _dbgSignals++;
                ExecuteEntry(-1, "APLUS_S", entry);
                return;
            }

            // ── SETUP B opcional: doble SI + confirmación ──
            if (EnableSiDouble)
            {
                if (SiBullZone(sig - 1) && siBull && BullConfirm(conf))
                {
                    _dbgSignals++; ExecuteEntry(+1, "SI_DOUBLE_L", entry); return;
                }
                if (SiBearZone(sig - 1) && siBear && BearConfirm(conf))
                {
                    _dbgSignals++; ExecuteEntry(-1, "SI_DOUBLE_S", entry); return;
                }
            }
        }

        // ════════════════ SEÑALES DESDE EL FOOTPRINT ════════════════
        // Stacked Imbalance verde en el 30% inferior de la vela => posible suelo.
        private bool SiBullZone(int bar)
        {
            if (bar < 0) return false;
            var c = GetCandle(bar);
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            int stacked = 0;
            decimal lowestImbPrice = c.High;
            foreach (var lvl in OrderedLevels(c))
            {
                // imbalance comprador: ask diagonal domina al bid del nivel inferior
                if (IsBuyImbalance(c, lvl))
                {
                    stacked++;
                    if (lvl.Price < lowestImbPrice) lowestImbPrice = lvl.Price;
                }
                else stacked = 0; // deben ser consecutivos

                if (stacked >= EffMinStacked)
                {
                    decimal rel = (lowestImbPrice - c.Low) / range;
                    if (rel <= EffZonePct) return true;  // zona inferior (laxo ensancha a 45%)
                }
            }
            return false;
        }

        // Stacked Imbalance rojo en el 30% superior => posible techo.
        private bool SiBearZone(int bar)
        {
            if (bar < 0) return false;
            var c = GetCandle(bar);
            decimal range = c.High - c.Low;
            if (range <= 0) return false;

            int stacked = 0;
            decimal highestImbPrice = c.Low;
            foreach (var lvl in OrderedLevels(c))
            {
                if (IsSellImbalance(c, lvl))
                {
                    stacked++;
                    if (lvl.Price > highestImbPrice) highestImbPrice = lvl.Price;
                }
                else stacked = 0;

                if (stacked >= EffMinStacked)
                {
                    decimal rel = (c.High - highestImbPrice) / range;
                    if (rel <= EffZonePct) return true;  // zona superior (laxo ensancha a 45%)
                }
            }
            return false;
        }

        // Absorción alcista: gran volumen concentrado cerca del mínimo que NO deja seguir cayendo.
        private bool AbsorptionBull(int bar)
        {
            if (bar < 1) return false;
            var c = GetCandle(bar);
            var prev = GetCandle(bar - 1);
            var maxLvl = MaxVolumeLevel(c);
            if (maxLvl == null) return false;

            bool bigVolLow = LevelVolume(maxLvl) >= AbsorptionVolMin
                             && (c.Low <= prev.Low)                 // probó el mínimo
                             && c.Close > c.Low + (c.High - c.Low) * 0.5m; // rechazó hacia arriba
            return bigVolLow;
        }

        private bool AbsorptionBear(int bar)
        {
            if (bar < 1) return false;
            var c = GetCandle(bar);
            var prev = GetCandle(bar - 1);
            var maxLvl = MaxVolumeLevel(c);
            if (maxLvl == null) return false;

            bool bigVolHigh = LevelVolume(maxLvl) >= AbsorptionVolMin
                              && (c.High >= prev.High)
                              && c.Close < c.High - (c.High - c.Low) * 0.5m;
            return bigVolHigh;
        }

        // Confirmación de precio: la vela actual rompe el extremo de la anterior.
        private bool BullConfirm(int bar)
        {
            var c = GetCandle(bar);
            var prev = GetCandle(bar - 1);
            return c.Close > c.Open && c.Close > prev.High;
        }

        private bool BearConfirm(int bar)
        {
            var c = GetCandle(bar);
            var prev = GetCandle(bar - 1);
            return c.Close < c.Open && c.Close < prev.Low;
        }

        // ════════════════ ACCESO AL FOOTPRINT (API ATAS.Indicators confirmada) ════════════════
        // Devuelve los niveles de precio ordenados de menor a mayor.
        private IEnumerable<PriceVolumeInfo> OrderedLevels(IndicatorCandle c)
        {
            var levels = c.GetAllPriceLevels();           // IEnumerable<PriceVolumeInfo>
            if (levels == null) yield break;
            foreach (var lvl in levels.OrderBy(l => l.Price))
                yield return lvl;
        }

        private PriceVolumeInfo? MaxVolumeLevel(IndicatorCandle c)
        {
            try { return c.MaxVolumePriceInfo; }          // PriceVolumeInfo del nivel de mayor volumen
            catch { return null; }
        }

        private decimal LevelVolume(PriceVolumeInfo lvl)
        {
            // PriceVolumeInfo.Volume = volumen total del nivel (bid+ask en .Bid / .Ask)
            return lvl.Volume;
        }

        // Tamaño de tick del instrumento. IndicatorCandle no expone Security;
        // el tick viene de InstrumentInfo (ATAS.Indicators.IInstrumentInfo.TickSize).
        private decimal Tick => InstrumentInfo?.TickSize ?? 0m;

        // Imbalance diagonal comprador: ask de este nivel vs bid del nivel inmediatamente inferior.
        // Imbalance diagonal comprador REAL: ask de este nivel vs bid del nivel inferior.
        // Exige ask>0 Y bid>0 (ambos reales) para calcular el ratio. Antes, si no había
        // bid inferior devolvía true → los bordes de la vela contaban como imbalance y
        // siBull saltaba en ~85% de las barras (el SI no filtraba nada).
        private bool IsBuyImbalance(IndicatorCandle c, PriceVolumeInfo lvl)
        {
            if (Tick <= 0) return false;
            decimal ask = lvl.Ask;
            if (ask <= 0) return false;
            var below = LevelAt(c, lvl.Price - Tick);
            decimal bid = below?.Bid ?? 0m;
            if (bid <= 0) return false;
            return ask >= bid * EffImbalanceRatio;
        }

        private bool IsSellImbalance(IndicatorCandle c, PriceVolumeInfo lvl)
        {
            if (Tick <= 0) return false;
            decimal bid = lvl.Bid;
            if (bid <= 0) return false;
            var above = LevelAt(c, lvl.Price + Tick);
            decimal ask = above?.Ask ?? 0m;
            if (ask <= 0) return false;
            return bid >= ask * EffImbalanceRatio;
        }

        private PriceVolumeInfo? LevelAt(IndicatorCandle c, decimal price)
        {
            try { return c.GetPriceVolumeInfo(price); }
            catch { return null; }
        }

        // ════════════════ EJECUCIÓN LIVE-GRADE (dirigida por FILLS) ════════════════
        // El estado de la posición se lleva por los FILLS reales (OnNewMyTrade), NO por
        // CurrentPosition: en Replay CurrentPosition puede leer 0 aunque haya posición y
        // OnCurrentPositionChanged no dispara. Flujo:
        //   ENTRY market  → al llegar su fill se coloca el bracket (SL+TP reales, OCO +
        //                   AutoCancel) al precio de fill real → stop en el lado correcto.
        //   fill con bracket ya puesto → es la salida (saltó SL o TP) → cerramos ciclo.
        private void ExecuteEntry(int dir, string label, decimal refPrice)
        {
            if (_entryPending || _posDir != 0) return;   // ya hay operación en curso

            _entryPending = true;
            _posDir = dir;
            _entryPrice = refPrice;       // fallback; se sustituye por el precio de fill REAL al colocar el bracket
            _initialRisk = StopPoints;
            _beActivated = false;
            _pendingLabel = label;
            _tradesToday++;

            // Solo entrada a mercado. El bracket SL+TP se coloca al confirmarse el FILL,
            // usando el precio de fill real (MyTrade.Price) → el stop siempre queda del
            // lado correcto. Usar el cierre de la vela daba "Wrong stop price" porque no
            // coincide con el precio real de ejecución.
            OpenOrder(new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = dir > 0 ? OrderDirections.Buy : OrderDirections.Sell,
                Type = OrderTypes.Market,
                QuantityToFill = Quantity,
                Comment = label
            });

            if (DebugMode)
                this.LogInfo($"[OFNQ] ENTRY {label} dir={dir} (ref {refPrice}, SL/TP al fill) trade#{_tradesToday}");
        }

        // Coloca SL + TP reales como grupo OCO. Guard: solo si no hay ya bracket.
        private void PlaceBracket()
        {
            if (_slOrder != null || _tpOrder != null) return;

            int dir = _posDir;
            decimal slLvl = _entryPrice - StopPoints * dir;
            decimal tpLvl = _entryPrice + StopPoints * TargetRR * dir;
            string grp = "OFNQ-" + (++_ocoCounter);
            var exitDir = dir > 0 ? OrderDirections.Sell : OrderDirections.Buy;

            _slOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = exitDir,
                Type = OrderTypes.Stop,
                TriggerPrice = slLvl,   // nivel de disparo del Stop (ATAS lo lee de aquí, no de Price)
                Price = slLvl,
                QuantityToFill = Quantity,
                OCOGroup = grp,
                AutoCancel = true,
                Comment = _pendingLabel + "_SL"
            };
            OpenOrder(_slOrder);

            _tpOrder = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = exitDir,
                Type = OrderTypes.Limit,
                Price = tpLvl,
                QuantityToFill = Quantity,
                OCOGroup = grp,
                AutoCancel = true,
                Comment = _pendingLabel + "_TP"
            };
            OpenOrder(_tpOrder);

            if (DebugMode)
                this.LogInfo($"[OFNQ] BRACKET dir={dir} entry={_entryPrice} SL={slLvl} TP={tpLvl} oco={grp}");
        }

        // Break-even: mueve el STOP REAL a entrada ± offset cuando el precio alcanza BeTriggerR.
        private void ManagePosition(IndicatorCandle candle)
        {
            if (_posDir == 0 || _slOrder == null) return;

            decimal price = candle.Close;
            decimal pnlR = _initialRisk > 0 ? ((price - _entryPrice) * _posDir) / _initialRisk : 0;

            if (!_beActivated && pnlR >= BeTriggerR)
            {
                _beActivated = true;
                decimal beLvl = _entryPrice + (BeOffsetPoints * _posDir);

                // ModifyOrder(viejo, nuevo): se construye la orden de reemplazo con el
                // nuevo precio, conservando OCOGroup para que siga enlazada al TP.
                var newSl = new Order
                {
                    Portfolio = Portfolio,
                    Security = Security,
                    Direction = _slOrder.Direction,
                    Type = OrderTypes.Stop,
                    TriggerPrice = beLvl,
                    Price = beLvl,
                    QuantityToFill = Quantity,
                    OCOGroup = _slOrder.OCOGroup,
                    AutoCancel = true,
                    Comment = _slOrder.Comment
                };
                ModifyOrder(_slOrder, newSl);
                _slOrder = newSl;
                if (DebugMode) this.LogInfo($"[OFNQ] BE move SL -> {beLvl}");
            }
        }

        // Se invoca al fill de salida (saltó SL o TP). Realiza PnL y resetea el ciclo.
        private void HandleFlat(decimal exitPrice)
        {
            if (_posDir == 0) return;

            decimal pts = (exitPrice - _entryPrice) * _posDir;
            _pnlTodayUsd += pts * DollarsPerPoint * Quantity;

            // Cortacircuitos: cuenta pérdidas seguidas (un cierre ganador o en BE resetea).
            if (pts < 0) _consecutiveLosses++; else _consecutiveLosses = 0;

            if (DebugMode)
            {
                this.LogInfo($"[OFNQ] EXIT exit={exitPrice} pts={pts:F2} pnlDay={_pnlTodayUsd:F2} lossStreak={_consecutiveLosses}");
                if (MaxConsecutiveLosses > 0 && _consecutiveLosses == MaxConsecutiveLosses)
                    this.LogInfo($"[OFNQ] Cortacircuitos activado: {_consecutiveLosses} pérdidas seguidas → sin más entradas hoy.");
            }

            CancelProtective();   // por si la OCO no canceló el hermano todavía
            _posDir = 0;
            _beActivated = false;
            _slOrder = null;
            _tpOrder = null;
        }

        private void CancelProtective()
        {
            foreach (var o in Orders)
                if (o != null && o.State == OrderStates.Active &&
                    (o.Type == OrderTypes.Stop || o.Type == OrderTypes.Limit))
                    CancelOrder(o);
        }

        // ════════════════ EVENTOS DE POSICIÓN / ÓRDENES ════════════════
        protected override void OnStarted()
        {
            _posDir = 0;
            _entryPending = false;
            _beActivated = false;
            _slOrder = null;
            _tpOrder = null;
            _historyDone = false;   // se rearma; el recálculo histórico aún no ha terminado
        }

        // Al parar la estrategia, cancela cualquier SL/TP resting para no dejar órdenes
        // colgando en la cuenta.
        protected override void OnStopped()
        {
            CancelProtective();
            _posDir = 0;
            _entryPending = false;
            _slOrder = null;
            _tpOrder = null;
        }

        // Habilita el trading en cuanto estamos en tiempo real, por dos vías (la que
        // ocurra antes): termina el recálculo histórico, o llega el primer dato de
        // mercado real (OnNewTrade NO se dispara durante el recálculo histórico).
        // Habilitamos trading SOLO con el primer TICK REAL (OnNewTrade), que únicamente
        // se dispara cuando el replay está reproduciendo datos. OnFinishRecalculate NO
        // sirve: marca "fin de recálculo" pero el replay puede no estar emitiendo ticks
        // aún, y entonces el market no se rellena.
        private void MarkRealtime()
        {
            if (_historyDone) return;
            _historyDone = true;
            if (DebugMode) this.LogInfo("[OFNQ] Tiempo real activo (primer tick real, trading habilitado).");
        }

        protected override void OnNewTrade(MarketDataArg trade) => MarkRealtime();

        // Toda la máquina de estados se dirige aquí, por FILLS reales (siempre se disparan).
        // Distinguimos entrada/salida por el SENTIDO del fill respecto a la operación, así
        // un fill duplicado (ATAS a veces emite el evento dos veces) nunca cierra por error.
        protected override void OnNewMyTrade(MyTrade trade)
        {
            if (trade == null) return;
            decimal price = trade.Price;

            bool fillIsBuy = trade.OrderDirection == OrderDirections.Buy;
            bool fillMatchesEntryDir = (fillIsBuy && _posDir > 0) || (!fillIsBuy && _posDir < 0);

            if (DebugMode) this.LogInfo($"[OFNQ] FILL {trade.OrderDirection} vol={trade.Volume} @ {price}");

            if (_posDir != 0 && _entryPending && _slOrder == null && fillMatchesEntryDir)
            {
                // Fill de la ENTRADA (mismo sentido que la operación) → bracket al precio real
                _entryPrice = price;
                _entryPending = false;
                PlaceBracket();
            }
            else if (_posDir != 0 && _slOrder != null && !fillMatchesEntryDir)
            {
                // Fill en sentido contrario con bracket ya puesto → salida (saltó SL o TP)
                HandleFlat(price);
            }
            // Cualquier otro caso (incluido un fill duplicado del mismo evento) se ignora.
        }

        // Solo diagnóstico: en este Replay CurrentPosition no es fiable, así que no
        // dirigimos lógica desde aquí (lo hace OnNewMyTrade por fills).
        protected override void OnCurrentPositionChanged()
        {
            if (DebugMode) this.LogInfo($"[OFNQ] POS CHANGED -> {CurrentPosition}");
        }

        protected override void OnOrderRegisterFailed(Order order, string message)
        {
            this.LogError($"[OFNQ] ORDER FAILED: {order?.Comment} -> {message}");
        }

        // ════════════════ SESIÓN / DEBUG ════════════════
        private bool InSession(IndicatorCandle c)
        {
            int h = c.Time.Hour; // VERIFY: Time suele venir en UTC; ajusta la franja a la tz del exchange
            return h >= SessionStartHour && h < SessionEndHour;
        }

        private void DumpDailyDebug()
        {
            this.LogInfo($"[OFNQ][DAY {_lastDate:yyyy-MM-dd}] bars={_dbgBars} " +
                         $"absBull={_dbgAbsBull} absBear={_dbgAbsBear} " +
                         $"siBull={_dbgSiBull} siBear={_dbgSiBear} signals={_dbgSignals} " +
                         $"trades={_tradesToday} pnl={_pnlTodayUsd:F2}");
        }
    }
}
