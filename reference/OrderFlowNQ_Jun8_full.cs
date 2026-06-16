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

        [DisplayName("Speed bars breakout")]
        public int SpeedBars { get; set; } = 2;

        [DisplayName("Extreme zone pct")]
        public decimal ExtremeZone { get; set; } = 0.35m;

        [DisplayName("BT min volume")]
        public double BT_VolMin { get; set; } = 30;

        [DisplayName("Require absorption")]
        public bool RequireAbsorption { get; set; } = true;

        [DisplayName("Absorption color switch signal")]
        public bool AbsColorSwitch { get; set; } = true;

        private int _tradesToday = 0;
        private decimal _pnlToday = 0m;
        private int _speedState = 0;
        private DateTime _lastDate = DateTime.MinValue;
        private int _lastBar = -1;
        private bool _inPosition = false;
        private decimal _entryPrice = 0m;
        private decimal _initialRisk = 0m;
        private int _posDir = 0;
        private bool _beActivated = false;
        private bool _prevAbsBullish = false;
        private bool _prevAbsBearish = false;

        private SpeedOfTape _sot = null!;
        private BigTrades _bt = null!;
        private StackedImbalance _si = null!;
        private Absorption _abs = null!;

        protected override void OnStarted()
        {
            _sot = new SpeedOfTape();
            _bt = new BigTrades();
            _si = new StackedImbalance();
            _abs = new Absorption();
            Add(_sot);
            Add(_bt);
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
                _prevAbsBullish = false;
                _prevAbsBearish = false;
            }

            if (_inPosition) { ManagePosition(bar, candle); return; }
            if (_tradesToday >= MaxTrades) return;
            if (_pnlToday <= -MaxDailyLoss) return;

            UpdateSpeedState(bar);
            bool rupturaBull = _speedState >= SpeedBars;
            bool rupturaBear = _speedState <= -SpeedBars;

            bool curAbsBull = IsAbsorptionBullish(bar - 1);
            bool curAbsBear = IsAbsorptionBearish(bar - 1);
            bool absColorSwitchLong = AbsColorSwitch && _prevAbsBearish && curAbsBull;
            bool absColorSwitchShort = AbsColorSwitch && _prevAbsBullish && curAbsBear;

            if (!rupturaBear && Setup1Long(bar, absColorSwitchLong)) goto UpdateAbs;
            if (!rupturaBull && Setup1Short(bar, absColorSwitchShort)) goto UpdateAbs;
            if (!rupturaBear && Setup2Long(bar, absColorSwitchLong)) goto UpdateAbs;
            if (!rupturaBull && Setup2Short(bar, absColorSwitchShort)) goto UpdateAbs;
            if (rupturaBull) Setup3Long(bar);
            if (rupturaBear) Setup3Short(bar);

        UpdateAbs:
            _prevAbsBullish = curAbsBull;
            _prevAbsBearish = curAbsBear;
        }

        private bool Setup1Long(int bar, bool absSwitch)
        {
            int b0 = bar - 1, b1 = bar - 2;
            if (!IsYellowBear(b1) || !IsYellowBull(b0)) return false;
            if (!ClusterLow(b0) || !ClusterLow(b1)) return false;
            if (!BTLow(b0) && !SILow(b0)) return false;
            bool absOk = IsAbsorptionBullish(b0) || absSwitch;
            if (RequireAbsorption && !absOk) return false;
            ExecuteLong(absSwitch ? "S1L_AbsSwitch" : "S1L");
            return true;
        }

        private bool Setup1Short(int bar, bool absSwitch)
        {
            int b0 = bar - 1, b1 = bar - 2;
            if (!IsYellowBull(b1) || !IsYellowBear(b0)) return false;
            if (!ClusterHigh(b0) || !ClusterHigh(b1)) return false;
            if (!BTHigh(b0) && !SIHigh(b0)) return false;
            bool absOk = IsAbsorptionBearish(b0) || absSwitch;
            if (RequireAbsorption && !absOk) return false;
            ExecuteShort(absSwitch ? "S1S_AbsSwitch" : "S1S");
            return true;
        }

        private bool Setup2Long(int bar, bool absSwitch)
        {
            int b0 = bar - 1;
            if (!IsLowSpike(b0)) return false;
            if (!BTLow(b0) || !SILow(b0)) return false;
            bool absOk = IsAbsorptionBullish(b0) || absSwitch;
            if (!absOk) return false;
            ExecuteLong(absSwitch ? "S2L_AbsSwitch_APLUS" : "S2L_APLUS");
            return true;
        }

        private bool Setup2Short(int bar, bool absSwitch)
        {
            int b0 = bar - 1;
            if (!IsHighSpike(b0)) return false;
            if (!BTHigh(b0) || !SIHigh(b0)) return false;
            bool absOk = IsAbsorptionBearish(b0) || absSwitch;
            if (!absOk) return false;
            ExecuteShort(absSwitch ? "S2S_AbsSwitch_APLUS" : "S2S_APLUS");
            return true;
        }

        private void Setup3Long(int bar)
        {
            int b0 = bar - 1;
            if (!BTLow(b0) && !SILow(b0)) return;
            if (RequireAbsorption && !IsAbsorptionBullish(b0)) return;
            ExecuteLong("S3L_Continuation");
        }

        private void Setup3Short(int bar)
        {
            int b0 = bar - 1;
            if (!BTHigh(b0) && !SIHigh(b0)) return;
            if (RequireAbsorption && !IsAbsorptionBearish(b0)) return;
            ExecuteShort("S3S_Continuation");
        }

        private void ManagePosition(int bar, IndicatorCandle candle)
        {
            decimal price = candle.Close;
            decimal pnl = (price - _entryPrice) * _posDir;
            decimal pnlR = _initialRisk > 0 ? pnl / _initialRisk : 0;

            if (!_beActivated && pnlR >= 1.0m) { _beActivated = true; MoveStopBE(); }
            if (pnlR >= 3.0m) { CloseAll("TP3R"); return; }

            bool reversal = _posDir > 0
                ? IsYellowBear(bar - 1) && IsYellowBear(bar - 2)
                : IsYellowBull(bar - 1) && IsYellowBull(bar - 2);
            if (reversal) { CloseAll("Reversal"); return; }

            decimal stopLevel = _beActivated
                ? _entryPrice + (0.25m * _posDir)
                : _entryPrice - (_initialRisk * _posDir);
            if ((price - stopLevel) * _posDir < 0) CloseAll("SL");
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
                    Price = _entryPrice + (0.25m * _posDir),
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

        private void ExecuteLong(string label)
        {
            if (_inPosition) return;
            decimal entry = GetCandle(_lastBar).Close;
            OpenOrder(new Order { Direction = OrderDirections.Buy, Type = OrderTypes.Market, QuantityToFill = 1m, Comment = label, Security = Security, Portfolio = Portfolio });
            OpenOrder(new Order { Direction = OrderDirections.Sell, Type = OrderTypes.Stop, Price = entry - StopPoints, QuantityToFill = 1m, Comment = label + "_SL", Security = Security, Portfolio = Portfolio });
            OpenOrder(new Order { Direction = OrderDirections.Sell, Type = OrderTypes.Limit, Price = entry + StopPoints * TargetRR, QuantityToFill = 1m, Comment = label + "_TP", Security = Security, Portfolio = Portfolio });
            _inPosition = true; _entryPrice = entry; _initialRisk = StopPoints; _posDir = 1; _beActivated = false; _tradesToday++;
        }

        private void ExecuteShort(string label)
        {
            if (_inPosition) return;
            decimal entry = GetCandle(_lastBar).Close;
            OpenOrder(new Order { Direction = OrderDirections.Sell, Type = OrderTypes.Market, QuantityToFill = 1m, Comment = label, Security = Security, Portfolio = Portfolio });
            OpenOrder(new Order { Direction = OrderDirections.Buy, Type = OrderTypes.Stop, Price = entry + StopPoints, QuantityToFill = 1m, Comment = label + "_SL", Security = Security, Portfolio = Portfolio });
            OpenOrder(new Order { Direction = OrderDirections.Buy, Type = OrderTypes.Limit, Price = entry - StopPoints * TargetRR, QuantityToFill = 1m, Comment = label + "_TP", Security = Security, Portfolio = Portfolio });
            _inPosition = true; _entryPrice = entry; _initialRisk = StopPoints; _posDir = -1; _beActivated = false; _tradesToday++;
        }

        private void UpdateSpeedState(int bar)
        {
            int b = bar - 1;
            if (IsYellowBull(b)) _speedState = Math.Min(_speedState + 1, 5);
            else if (IsYellowBear(b)) _speedState = Math.Max(_speedState - 1, -5);
            else _speedState = 0;
        }

        private bool IsYellowBull(int b)
        {
            if (b < 1) return false;
            return GetDs(_sot, 0, b) > 0 && GetDs(_sot, 1, b) > GetDs(_sot, 1, b - 1);
        }

        private bool IsYellowBear(int b)
        {
            if (b < 1) return false;
            return GetDs(_sot, 0, b) > 0 && GetDs(_sot, 1, b) < GetDs(_sot, 1, b - 1);
        }

        private bool ClusterLow(int b)
        {
            if (b < 0) return false;
            var c = GetCandle(b);
            decimal range = (decimal)(c.High - c.Low);
            if (range == 0) return false;
            double cl = GetDs(_sot, 2, b);
            if (cl == 0) return false;
            return ((decimal)cl - (decimal)c.Low) / range <= ExtremeZone;
        }

        private bool ClusterHigh(int b)
        {
            if (b < 0) return false;
            var c = GetCandle(b);
            decimal range = (decimal)(c.High - c.Low);
            if (range == 0) return false;
            double cl = GetDs(_sot, 2, b);
            if (cl == 0) return false;
            return ((decimal)c.High - (decimal)cl) / range <= ExtremeZone;
        }

        private bool BTLow(int b)
        {
            if (b < 0) return false;
            double v = GetDs(_bt, 0, b);
            return v < 0 && Math.Abs(v) >= BT_VolMin;
        }

        private bool BTHigh(int b)
        {
            if (b < 0) return false;
            double v = GetDs(_bt, 0, b);
            return v > 0 && v >= BT_VolMin;
        }

        private bool SILow(int b) => b >= 0 && GetDs(_si, 0, b) < 0;
        private bool SIHigh(int b) => b >= 0 && GetDs(_si, 0, b) > 0;

        private bool IsAbsorptionBullish(int b)
        {
            if (b < 0) return false;
            try { return GetDs(_abs, 0, b) > 0 || GetDs(_abs, 2, b) > 0; }
            catch { return false; }
        }

        private bool IsAbsorptionBearish(int b)
        {
            if (b < 0) return false;
            try { return GetDs(_abs, 1, b) > 0 || GetDs(_abs, 3, b) > 0; }
            catch { return false; }
        }

        private bool IsLowSpike(int b)
        {
            if (b < 0) return false;
            var c = GetCandle(b);
            decimal body = Math.Abs((decimal)(c.Close - c.Open));
            if (body == 0) body = 0.25m;
            return (decimal)(Math.Min(c.Open, c.Close) - c.Low) >= body * 1.5m;
        }

        private bool IsHighSpike(int b)
        {
            if (b < 0) return false;
            var c = GetCandle(b);
            decimal body = Math.Abs((decimal)(c.Close - c.Open));
            if (body == 0) body = 0.25m;
            return (decimal)(c.High - Math.Max(c.Open, c.Close)) >= body * 1.5m;
        }

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
