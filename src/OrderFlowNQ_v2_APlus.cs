using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Strategies.Chart;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        public decimal StopPoints { get; set; } = 3.0m;

        [Display(Name = "Target RR", GroupName = "Risk", Order = 3)]
        public decimal TargetRR { get; set; } = 2.0m;

        [Display(Name = "Break-even trigger (R)", GroupName = "Risk", Order = 4)]
        public decimal BeTriggerR { get; set; } = 1.0m;

        [Display(Name = "Break-even offset (points)", GroupName = "Risk", Order = 5)]
        public decimal BeOffsetPoints { get; set; } = 0.1m;

        [Display(Name = "Force close at (R)", GroupName = "Risk", Order = 6)]
        public decimal ForceCloseR { get; set; } = 3.0m;

        [Display(Name = "$ per point per contract", GroupName = "Risk", Order = 7)]
        public decimal DollarsPerPoint { get; set; } = 2.0m; // MNQ = 2.0, MES = 5.0, NQ = 20.0

        [Display(Name = "Max trades / day", GroupName = "Risk", Order = 8)]
        public int MaxTrades { get; set; } = 20;

        [Display(Name = "Max daily loss USD", GroupName = "Risk", Order = 9)]
        public decimal MaxDailyLoss { get; set; } = 5000m;

        // ════════════════ PARÁMETROS — SEÑAL (para optimizar) ════════════════
        [Display(Name = "SI extreme zone %", GroupName = "Signal", Order = 20)]
        public decimal ZonePct { get; set; } = 0.30m;            // 30% inferior/superior

        [Display(Name = "Imbalance ratio", GroupName = "Signal", Order = 21)]
        public decimal ImbalanceRatio { get; set; } = 3.0m;      // ask vs bid diagonal

        [Display(Name = "Min stacked levels", GroupName = "Signal", Order = 22)]
        public int MinStackedLevels { get; set; } = 3;           // nº de niveles consecutivos

        [Display(Name = "Absorption volume min", GroupName = "Signal", Order = 23)]
        public decimal AbsorptionVolMin { get; set; } = 200m;    // volumen en el nivel clave

        [Display(Name = "Absorption lookback (bars)", GroupName = "Signal", Order = 24)]
        public int AbsorptionLookback { get; set; } = 3;         // vigencia de la absorción

        [Display(Name = "Require absorption", GroupName = "Signal", Order = 25)]
        public bool RequireAbsorption { get; set; } = true;

        [Display(Name = "Enable SI-double setup", GroupName = "Signal", Order = 26)]
        public bool EnableSiDouble { get; set; } = false;

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
        private DateTime _lastDate = DateTime.MinValue;
        private int _lastBar = -1;

        private bool _inPosition;
        private decimal _entryPrice;
        private decimal _initialRisk;
        private int _posDir;               // +1 long, -1 short
        private bool _beActivated;

        private int _absBullBar = -1;
        private int _absBearBar = -1;

        // contadores de diagnóstico (se vuelcan al log al cerrar sesión)
        private int _dbgBars, _dbgAbsBull, _dbgAbsBear, _dbgSiBull, _dbgSiBear, _dbgSignals;

        public OrderFlowNQ_v2()
        {
            // La estrategia no dibuja nada: no necesitamos DataSeries propias.
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == _lastBar) return;
            if (bar < 5) return;
            if (!CanProcess(bar)) return;          // VERIFY: gate nativo de ATAS
            _lastBar = bar;

            var candle = GetCandle(bar);
            var today = candle.Time.Date;

            if (today != _lastDate)
            {
                if (DebugMode && _lastDate != DateTime.MinValue) DumpDailyDebug();
                _tradesToday = 0;
                _pnlTodayUsd = 0m;
                _lastDate = today;
                _inPosition = false;
                _beActivated = false;
                _absBullBar = -1;
                _absBearBar = -1;
                _dbgBars = _dbgAbsBull = _dbgAbsBear = _dbgSiBull = _dbgSiBear = _dbgSignals = 0;
            }

            _dbgBars++;

            if (_inPosition) { ManagePosition(candle); return; }

            // Cortacircuitos diarios
            if (_tradesToday >= MaxTrades) return;
            if (_pnlTodayUsd <= -MaxDailyLoss) return;

            if (UseSessionFilter && !InSession(candle)) return;

            // ── Señales del footprint sobre la vela ya cerrada (bar - 1) ──
            int sig = bar - 1;

            if (AbsorptionBull(sig)) { _absBullBar = sig; _dbgAbsBull++; }
            if (AbsorptionBear(sig)) { _absBearBar = sig; _dbgAbsBear++; }

            bool siBull = SiBullZone(sig); if (siBull) _dbgSiBull++;
            bool siBear = SiBearZone(sig); if (siBear) _dbgSiBear++;

            // ── SETUP A+ LONG: absorción reciente + SI verde en zona baja + confirmación ──
            bool absBullOk = !RequireAbsorption || (_absBullBar >= 0 && sig - _absBullBar <= AbsorptionLookback);
            if (absBullOk && siBull && BullConfirm(bar))
            {
                _dbgSignals++;
                ExecuteEntry(+1, "APLUS_L", candle.Close);
                return;
            }

            // ── SETUP A+ SHORT ──
            bool absBearOk = !RequireAbsorption || (_absBearBar >= 0 && sig - _absBearBar <= AbsorptionLookback);
            if (absBearOk && siBear && BearConfirm(bar))
            {
                _dbgSignals++;
                ExecuteEntry(-1, "APLUS_S", candle.Close);
                return;
            }

            // ── SETUP B opcional: doble SI + confirmación ──
            if (EnableSiDouble)
            {
                if (SiBullZone(bar - 2) && siBull && BullConfirm(bar))
                {
                    _dbgSignals++; ExecuteEntry(+1, "SI_DOUBLE_L", candle.Close); return;
                }
                if (SiBearZone(bar - 2) && siBear && BearConfirm(bar))
                {
                    _dbgSignals++; ExecuteEntry(-1, "SI_DOUBLE_S", candle.Close); return;
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

                if (stacked >= MinStackedLevels)
                {
                    decimal rel = (lowestImbPrice - c.Low) / range;
                    if (rel <= ZonePct) return true;     // en el 30% inferior
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

                if (stacked >= MinStackedLevels)
                {
                    decimal rel = (c.High - highestImbPrice) / range;
                    if (rel <= ZonePct) return true;     // en el 30% superior
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

        // ════════════════ ACCESO AL FOOTPRINT (VERIFY contra tu ATAS v8) ════════════════
        // Devuelve los niveles de precio ordenados de menor a mayor.
        private IEnumerable<PriceVolumeInfo> OrderedLevels(IndicatorCandle c)
        {
            var levels = c.GetAllPriceLevels();           // VERIFY: nombre del método
            if (levels == null) yield break;
            foreach (var lvl in levels.OrderBy(l => l.Price))
                yield return lvl;
        }

        private PriceVolumeInfo MaxVolumeLevel(IndicatorCandle c)
        {
            try { return c.MaxVolumePriceInfo; }          // VERIFY
            catch { return null; }
        }

        private decimal LevelVolume(PriceVolumeInfo lvl)
        {
            // VERIFY: en algunas versiones es .Volume; bid/ask por separado en .Bid / .Ask
            return lvl.Volume;
        }

        // Imbalance diagonal comprador: ask de este nivel vs bid del nivel inmediatamente inferior.
        private bool IsBuyImbalance(IndicatorCandle c, PriceVolumeInfo lvl)
        {
            var below = LevelAt(c, lvl.Price - c.Security?.TickSize ?? lvl.Price);
            decimal bid = below?.Bid ?? 0m;               // VERIFY .Bid / .Ask
            decimal ask = lvl.Ask;
            if (bid <= 0) return ask > 0;
            return ask >= bid * ImbalanceRatio;
        }

        private bool IsSellImbalance(IndicatorCandle c, PriceVolumeInfo lvl)
        {
            var above = LevelAt(c, lvl.Price + (c.Security?.TickSize ?? 0m));
            decimal ask = above?.Ask ?? 0m;
            decimal bid = lvl.Bid;
            if (ask <= 0) return bid > 0;
            return bid >= ask * ImbalanceRatio;
        }

        private PriceVolumeInfo LevelAt(IndicatorCandle c, decimal price)
        {
            try { return c.GetPriceVolumeInfo(price); }   // VERIFY
            catch { return null; }
        }

        // ════════════════ EJECUCIÓN ════════════════
        private void ExecuteEntry(int dir, string label, decimal entry)
        {
            if (_inPosition) return;

            var mkt = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = dir > 0 ? OrderDirections.Buy : OrderDirections.Sell,
                Type = OrderTypes.Market,
                QuantityToFill = Quantity,
                Comment = label
            };
            OpenOrder(mkt);

            var sl = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = dir > 0 ? OrderDirections.Sell : OrderDirections.Buy,
                Type = OrderTypes.Stop,
                Price = entry - StopPoints * dir,
                QuantityToFill = Quantity,
                Comment = label + "_SL"
            };
            OpenOrder(sl);

            var tp = new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = dir > 0 ? OrderDirections.Sell : OrderDirections.Buy,
                Type = OrderTypes.Limit,
                Price = entry + StopPoints * TargetRR * dir,
                QuantityToFill = Quantity,
                Comment = label + "_TP"
            };
            OpenOrder(tp);

            _inPosition = true;
            _entryPrice = entry;
            _initialRisk = StopPoints;
            _posDir = dir;
            _beActivated = false;
            _tradesToday++;

            if (DebugMode)
                this.LogInfo($"[OFNQ] ENTRY {label} dir={dir} @ {entry} SL={sl.Price} TP={tp.Price} trade#{_tradesToday}");
        }

        private void ManagePosition(IndicatorCandle candle)
        {
            decimal price = candle.Close;
            decimal pnl = (price - _entryPrice) * _posDir;
            decimal pnlR = _initialRisk > 0 ? pnl / _initialRisk : 0;

            if (!_beActivated && pnlR >= BeTriggerR)
            {
                _beActivated = true;
                MoveStopBE();
            }

            if (pnlR >= ForceCloseR) { CloseAll("TP" + ForceCloseR + "R", price); return; }

            decimal stopLevel = _beActivated
                ? _entryPrice + (BeOffsetPoints * _posDir)
                : _entryPrice - (_initialRisk * _posDir);

            if ((price - stopLevel) * _posDir < 0) CloseAll("SL", price);
        }

        private void MoveStopBE()
        {
            foreach (var order in Orders)
            {
                if (order.State != OrderStates.Active) continue;
                if (order.Type != OrderTypes.Stop) continue;
                CancelOrder(order);
                OpenOrder(new Order
                {
                    Portfolio = Portfolio,
                    Security = Security,
                    Direction = order.Direction,
                    Type = OrderTypes.Stop,
                    Price = _entryPrice + (BeOffsetPoints * _posDir),
                    QuantityToFill = order.QuantityToFill,
                    Comment = "BE"
                });
                break;
            }
        }

        private void CloseAll(string reason, decimal exitPrice)
        {
            foreach (var order in Orders)
                if (order.State == OrderStates.Active)
                    CancelOrder(order);

            OpenOrder(new Order
            {
                Portfolio = Portfolio,
                Security = Security,
                Direction = _posDir > 0 ? OrderDirections.Sell : OrderDirections.Buy,
                Type = OrderTypes.Market,
                QuantityToFill = Quantity,
                Comment = "Close_" + reason
            });

            // Actualiza PnL diario (esto faltaba en la versión de Juan)
            decimal pts = (exitPrice - _entryPrice) * _posDir;
            _pnlTodayUsd += pts * DollarsPerPoint * Quantity;

            if (DebugMode)
                this.LogInfo($"[OFNQ] CLOSE {reason} exit={exitPrice} pts={pts:F2} pnlDay={_pnlTodayUsd:F2}");

            _inPosition = false;
            _beActivated = false;
        }

        // ════════════════ FALLOS DE ORDEN (antes invisibles) ════════════════
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
