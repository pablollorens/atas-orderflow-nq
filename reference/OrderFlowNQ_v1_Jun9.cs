using ATAS.DataFeedsCore;
using ATAS.Indicators;
using ATAS.Indicators.Technical;
using ATAS.Strategies.Chart;
using System;
using System.ComponentModel;

namespace OrderFlowNQ
{
    [DisplayName("OrderFlowNQ_v1")]
    public class OrderFlowNQ : ChartStrategy
    {
        [DisplayName("Stop points")]
        public decimal StopPoints { get; set; } = 3.0m;

        [DisplayName("Target RR")]
        public decimal TargetRR { get; set; } = 2.0m;

        [DisplayName("Max trades per day")]
        public int MaxTrades { get; set; } = 20;

        [DisplayName("Max daily loss USD")]
        public decimal MaxDailyLoss { get; set; } = 5000m;

        [DisplayName("Confirmation bars")]
        public int ConfirmBars { get; set; } = 2;

        private int _tradesToday = 0;
        private decimal _pnlToday = 0m;
        private DateTime _lastDate = DateTime.MinValue;
        private int _lastBar = -1;

        private bool _inPosition = false;
        private decimal _entryPrice = 0m;
        private decimal _initialRisk = 0m;
        private int _posDir = 0;
        private bool _beActivated = false;

        // Estado para detectar absorción previa
        private int _absBullBar = -1;
        private int _absBearBar = -1;

        private StackedImbalance _si = null!;
        private Absorption _abs = null!;

        protected override void OnStarted()
        {
            _si = new StackedImbalance();
            _abs = new Absorption();
            Add(_si);
            Add(_abs);
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar == _lastBar) return;
            if (bar < 5) return;
            if (!CanProcess(bar)) return;
            _lastBar = bar;

            var candle = GetCandle(bar);
            var today = candle.Time.Date;

            if (today != _lastDate)
            {
                _tradesToday = 0;
                _pnlToday = 0m;
                _lastDate = today;
                _inPosition = false;
                _beActivated = false;
                _absBullBar = -1;
                _absBearBar = -1;
            }

            if (_inPosition)
            {
                ManagePosition(bar, candle);
                return;
            }

            if (_tradesToday >= MaxTrades) return;
            if (_pnlToday <= -MaxDailyLoss) return;

            // Registrar absorción cuando aparece
            if (AbsBull(bar - 1)) _absBullBar = bar - 1;
            if (AbsBear(bar - 1)) _absBearBar = bar - 1;

            // SETUP A+ (EL REY): Absorción + SI + Confirmación
            // LONG: absorción reciente + SI verde + precio recupera
            if (_absBullBar >= 0 && bar - _absBullBar <= 3)
            {
                if (SIBull(bar - 1) && IsBullConfirm(bar))
                {
                    ExecuteLong("APLUS_L");
                    return;
                }
            }

            // SHORT: absorción reciente + SI rojo + precio pierde nivel
            if (_absBearBar >= 0 && bar - _absBearBar <= 3)
            {
                if (SIBear(bar - 1) && IsBearConfirm(bar))
                {
                    ExecuteShort("APLUS_S");
                    return;
                }
            }

            // SETUP B: SI solo con confirmación fuerte (2 velas)
            // LONG
            if (SIBull(bar - 2) && SIBull(bar - 1) && IsBullConfirm(bar))
            {
                ExecuteLong("SI_DOUBLE_L");
                return;
            }

            // SHORT
            if (SIBear(bar - 2) && SIBear(bar - 1) && IsBearConfirm(bar))
            {
                ExecuteShort("SI_DOUBLE_S");
                return;
            }
        }

        // ═══ CONFIRMACIONES ═══════════════════════════════════════
        // Bull confirm: vela alcista que cierra por encima de apertura
        private bool IsBullConfirm(int bar)
        {
            if (bar < 1) return false;
            var c = GetCandle(bar - 1);
            return c.Close > c.Open && c.Close > GetCandle(bar - 2).High;
        }

        // Bear confirm: vela bajista que cierra por debajo de apertura
        private bool IsBearConfirm(int bar)
        {
            if (bar < 1) return false;
            var c = GetCandle(bar - 1);
            return c.Close < c.Open && c.Close < GetCandle(bar - 2).Low;
        }

        // ═══ GESTIÓN DE POSICIÓN ══════════════════════════════════
        private void ManagePosition(int bar, IndicatorCandle candle)
        {
            decimal price = candle.Close;
            decimal pnl = (price - _entryPrice) * _posDir;
            decimal pnlR = _initialRisk > 0 ? pnl / _initialRisk : 0;

            // Break even a 1R
            if (!_beActivated && pnlR >= 1.0m)
            {
                _beActivated = true;
                MoveStopBE();
            }

            // TP a 3R
            if (pnlR >= 3.0m)
            {
                CloseAll("TP3R");
                return;
            }

            // Stop dinámico
            decimal stopLevel = _beActivated
                ? _entryPrice + (0.1m * _posDir)
                : _entryPrice - (_initialRisk * _posDir);

            if ((price - stopLevel) * _posDir < 0)
                CloseAll("SL");
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
                    Direction = order.Direction,
                    Type = OrderTypes.Stop,
                    Price = _entryPrice + (0.1m * _posDir),
                    QuantityToFill = order.QuantityToFill,
                    Security = Security,
                    Portfolio = Portfolio
                });
                break;
            }
        }

        private void CloseAll(string reason)
        {
            foreach (var order in Orders)
                if (order.State == OrderStates.Active)
                    CancelOrder(order);

            OpenOrder(new Order
            {
                Direction = _posDir > 0 ? OrderDirections.Sell : OrderDirections.Buy,
                Type = OrderTypes.Market,
                QuantityToFill = 1m,
                Comment = "Close_" + reason,
                Security = Security,
                Portfolio = Portfolio
            });

            _inPosition = false;
            _beActivated = false;
        }

        // ═══ EJECUCIÓN ════════════════════════════════════════════
        private void ExecuteLong(string label)
        {
            if (_inPosition) return;
            decimal entry = GetCandle(_lastBar).Close;

            OpenOrder(new Order
            {
                Direction = OrderDirections.Buy,
                Type = OrderTypes.Market,
                QuantityToFill = 1m,
                Comment = label,
                Security = Security,
                Portfolio = Portfolio
            });
            OpenOrder(new Order
            {
                Direction = OrderDirections.Sell,
                Type = OrderTypes.Stop,
                Price = entry - StopPoints,
                QuantityToFill = 1m,
                Comment = label + "_SL",
                Security = Security,
                Portfolio = Portfolio
            });
            OpenOrder(new Order
            {
                Direction = OrderDirections.Sell,
                Type = OrderTypes.Limit,
                Price = entry + StopPoints * TargetRR,
                QuantityToFill = 1m,
                Comment = label + "_TP",
                Security = Security,
                Portfolio = Portfolio
            });

            _inPosition = true;
            _entryPrice = entry;
            _initialRisk = StopPoints;
            _posDir = 1;
            _beActivated = false;
            _tradesToday++;
        }

        private void ExecuteShort(string label)
        {
            if (_inPosition) return;
            decimal entry = GetCandle(_lastBar).Close;

            OpenOrder(new Order
            {
                Direction = OrderDirections.Sell,
                Type = OrderTypes.Market,
                QuantityToFill = 1m,
                Comment = label,
                Security = Security,
                Portfolio = Portfolio
            });
            OpenOrder(new Order
            {
                Direction = OrderDirections.Buy,
                Type = OrderTypes.Stop,
                Price = entry + StopPoints,
                QuantityToFill = 1m,
                Comment = label + "_SL",
                Security = Security,
                Portfolio = Portfolio
            });
            OpenOrder(new Order
            {
                Direction = OrderDirections.Buy,
                Type = OrderTypes.Limit,
                Price = entry - StopPoints * TargetRR,
                QuantityToFill = 1m,
                Comment = label + "_TP",
                Security = Security,
                Portfolio = Portfolio
            });

            _inPosition = true;
            _entryPrice = entry;
            _initialRisk = StopPoints;
            _posDir = -1;
            _beActivated = false;
            _tradesToday++;
        }

        // ═══ INDICADORES ══════════════════════════════════════════
        private bool AbsBull(int b)
        {
            if (b < 0) return false;
            try { return GetDs(_abs, 0, b) > 0 || GetDs(_abs, 2, b) > 0; }
            catch { return false; }
        }

        private bool AbsBear(int b)
        {
            if (b < 0) return false;
            try { return GetDs(_abs, 1, b) > 0 || GetDs(_abs, 3, b) > 0; }
            catch { return false; }
        }

        private bool SIBull(int b)
        {
            if (b < 0) return false;
            return GetDs(_si, 0, b) > 0;
        }

        private bool SIBear(int b)
        {
            if (b < 0) return false;
            return GetDs(_si, 0, b) < 0;
        }

        // ═══ HELPER ═══════════════════════════════════════════════
        private double GetDs(object ind, int series, int b)
        {
            try
            {
                var i = ind as Indicator;
                if (i == null || series >= i.DataSeries.Count) return 0;
                var ds = i.DataSeries[series] as ValueDataSeries;
                if (ds == null) return 0;
                return (double)ds[b];
            }
            catch { return 0; }
        }
    }
}
