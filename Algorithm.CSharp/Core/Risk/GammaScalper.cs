using System;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class GammaScalper
    {
        private readonly Foundations _algo;
        public readonly Equity Equity;
        public readonly Symbol Symbol;

        private decimal _startingPrice;
        private decimal _triggerPriceHigh;
        private decimal _triggerPriceLow;
        private readonly decimal _trailingPct;
        private readonly bool _gammaScalpingEnabled;

        /// <summary>
        /// Constructor for Pos0 off Portfolio Holdings.
        /// </summary>
        public GammaScalper(Foundations algo, Equity equity)
        {
            _algo = algo;
            Equity = equity;
            Symbol = equity.Symbol;
            _trailingPct = _algo.Cfg.TrailingHedgePct.TryGetValue(Symbol, out _trailingPct) ? _trailingPct : _algo.Cfg.TrailingHedgePct[CfgDefault];
            _gammaScalpingEnabled = _algo.Cfg.GammaScalpingEnabled.TryGetValue(Symbol, out _gammaScalpingEnabled) ? _gammaScalpingEnabled : _algo.Cfg.GammaScalpingEnabled[CfgDefault];

            if (_gammaScalpingEnabled)
            {
                _algo.Log($"{_algo.Time} GammaScalper: Initializing for {Symbol}");
                _algo.TradeEventHandler += OnTradeEventUpdateHedgeState;
                _algo.NewBidAskEventHandler += OnNewBidAskEvent;
            }
        }
        public bool IsScalping { get; internal set; }
        public bool ExecuteHedge { get; internal set; }
        private decimal MidPrice { get => _algo.MidPrice(Symbol); }


        /// <summary>
        /// Goal: When gamma > 0 and delta ~= 0. Let runners run and profit from delta moving in our favor.
        /// Stop scalping when:
        ///     - Gamma goes <= 0
        ///     - Delta has not been hedged to ~=0 before scalping started
        ///     
        /// Restart scalping when:
        ///     - A new option trade significantly altered delta => pocket gains from on-going scalping
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void OnTradeEventUpdateHedgeState(object? sender, TradeEventArgs e)
        {
            foreach (Trade t in e.Trades)
            {
                if (Underlying(t.Symbol) != Symbol)
                {
                    continue;
                }

                if (t.SecurityType == SecurityType.Option)
                {
                    // Restart scalping after hedging given the new option fill altered delta significantly,
                    // UNLESS band threshold is not exceeeded.
                    if (_algo.PfRisk.IsUnderlyingDeltaExceedingBand(Symbol, _algo.DeltaMV(Symbol))) // The way of checking needs to be centralized...
                    {
                        Reset();
                        _algo.Log($"{_algo.Time} GammaScalper.OnTradeEventUpdateHedgeState: Noticed option fill for {t.Symbol} and delta exceeding band. Resetting any scalping.");
                        _algo.PfRisk.CheckHandleDeltaRiskExceedingBand(Symbol);  // Event elsewhere raised. Probably doesnt hurt raising again...
                        break;
                    }
                    // Continue as usual...
                }

                double gammaTotal = (double)_algo.PfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
                if (gammaTotal <= 0)
                {
                    Reset();
                    break;
                }

                decimal deltaTotal = _algo.DeltaMV(Symbol);
                if (_algo.PfRisk.IsUnderlyingDeltaExceedingBand(Symbol, deltaTotal) && !IsScalping)
                {
                    Reset();
                    break;
                }

                // Start Scalping - Start tracking the position
                // Improvement: Use the Consecutive Ticks Indicator.
                if (gammaTotal > 0 && !IsScalping)
                {
                    SetNewTriggerPrices();
                }
                else if (gammaTotal > 0 && IsScalping)
                {
                    UpdateTriggerPrices();
                }

                if (IsTriggered())
                {
                    Hedge();
                }
            }
        }

        private void OnNewBidAskEvent(object? sender, NewBidAskEventArgs newBidAsk)
        {
            if (newBidAsk.Symbol != Symbol) return;

            if (IsScalping)
            {
                UpdateTriggerPrices();
                if (IsTriggered())
                {
                    Hedge();
                }
            }
        }
        private void Reset()
        {
            IsScalping = false;
            _triggerPriceHigh = 0;
            _triggerPriceLow = 0;
            _startingPrice = 0;
        }

        private void SetNewTriggerPrices()
        {
            _startingPrice = MidPrice;
            _triggerPriceHigh = MidPrice * (1 + _trailingPct);
            _triggerPriceLow = MidPrice * (1 - _trailingPct);
            _algo.Log($"{_algo.Time} GammaScalper.SetNewTriggerPrices: Symbol={Symbol}, StartingPrice=MidPrice={MidPrice}, TriggerHigh={_triggerPriceHigh}, TriggerLow={_triggerPriceLow}");
        }
        private void UpdateTriggerPrices()
        {
            var newTriggerPriceHigh = Math.Max(_triggerPriceHigh, MidPrice * (1 + _trailingPct));
            var newTriggerPriceLow = Math.Min(_triggerPriceLow, MidPrice * (1 - _trailingPct));
            if (newTriggerPriceHigh != _triggerPriceHigh || newTriggerPriceLow != _triggerPriceLow)
            {
                _algo.Log($"{_algo.Time} GammaScalper.UpdateTriggerPrices: Symbol={Symbol}, MidPrice={MidPrice}, TriggerHighNew={newTriggerPriceHigh}, " +
                    $"TriggerHighOld={_triggerPriceHigh}, TriggerLowOld={_triggerPriceLow}, TriggerLowNew={newTriggerPriceLow}, StartingPrice={_startingPrice}, ScalpingGains={ScalpingGains()}");
                _triggerPriceHigh = newTriggerPriceHigh;
                _triggerPriceLow = newTriggerPriceLow;
            }
        }

        private bool IsTriggered() => (MidPrice >= _triggerPriceHigh || MidPrice <= _triggerPriceLow);

        /// <summary>
        /// If range bound, frequent hedging of tiny delta moves can incur more transaction costs than the gamma scalping profits.
        /// Hence check, if delta deviation from zero is greater than expected fee, hedge, otherwise only hedge if band is exceeded.
        /// </summary>
        private void Hedge()
        {
            bool deltaRiskExceedingBand = _algo.PfRisk.CheckHandleDeltaRiskExceedingBand(Symbol);
            bool scalpingGainsExceedTransactionCosts = ScalpingGainsExceedTransactionCosts();
            if (deltaRiskExceedingBand || scalpingGainsExceedTransactionCosts)
            {
                _algo.Log($"{_algo.Time} GammaScalper.Hedge: Symbol={Symbol}, MidPrice={MidPrice}, TriggerHigh={_triggerPriceHigh}, TriggerLow={_triggerPriceLow}, " +
                    $"scalpingGainsExceedTransactionCosts={scalpingGainsExceedTransactionCosts}, deltaRiskExceedingBand={deltaRiskExceedingBand}");
                _algo.ExecuteHedge(Symbol, _algo.EquityHedgeQuantity(Symbol), OrderType.Limit);
            }


        }

        private decimal ScalpingGains()
        {
            decimal gammaTotal = _algo.PfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            decimal scalpedDelta = gammaTotal * (MidPrice - _startingPrice);
            return scalpedDelta * (MidPrice - _startingPrice);
        }

        private bool ScalpingGainsExceedTransactionCosts()
        {
            decimal deltaTotal = _algo.DeltaMV(Symbol);

            decimal gammaTotal = _algo.PfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            decimal scalpedDelta = gammaTotal * (MidPrice - _startingPrice);            
            decimal gammaScalpingProfit = scalpedDelta * (MidPrice - _startingPrice);
            decimal transactionCosts = _algo.TransactionCosts(Symbol, scalpedDelta);
            _algo.Log($"{_algo.Time} GammaScalper.ScalpingGainsExceedTransactionCosts: Symbol={Symbol}, deltaTotal(notInCalc)={deltaTotal}, scalpedDelta={scalpedDelta}, gammaTotal={deltaTotal}, gammaScalpingProfit={gammaScalpingProfit}, transactionCosts={transactionCosts}");
            return gammaScalpingProfit > transactionCosts;
        }
    }
}
