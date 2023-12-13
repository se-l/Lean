using System;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using static QuantConnect.Messages;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class GammaScalper
    {
        private readonly Foundations _algo;
        public readonly Equity Equity;
        public readonly Symbol Symbol;
        public decimal TriggerPrice => _triggerPrice;

        private decimal _startingPrice;
        private decimal _triggerPrice;
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
        public OrderDirection ScalpingDirection { get; internal set; }
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
                ReviewScalper(t.Symbol);                
            }
        }

        private void ReviewScalper(Symbol symbol)
        {
            decimal deltaTotal = _algo.DeltaMV(Symbol);
            if (symbol.SecurityType == SecurityType.Option)
            {
                // Restart scalping after hedging given the new option fill altered delta significantly,
                // UNLESS band threshold is not exceeeded.
                if (
                    !IsScalping && _algo.PfRisk.IsUnderlyingDeltaExceedingBand(Symbol, _algo.DeltaMV(Symbol)) ||  // hedge to delta zero before starting to scalp.// The way of checking needs to be centralized...
                    ScalpingDirection != Num2Direction(deltaTotal)  // around delta zero or after a trade, scalping direction may have changed
                )
                {
                    Reset();
                    _algo.Log($"{_algo.Time} GammaScalper.OnTradeEventUpdateHedgeState: Noticed option fill for {symbol} and delta exceeding band. Resetting any scalping.");
                    _algo.PfRisk.CheckHandleDeltaRiskExceedingBand(Symbol);  // Event elsewhere raised. Probably doesnt hurt raising again...
                    return;
                }
                // Continue as usual...
            }

            double gammaTotal = (double)_algo.PfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            if (gammaTotal <= 0)
            {
                Reset();
                return;
            }

            if (_algo.PfRisk.IsUnderlyingDeltaExceedingBand(Symbol, deltaTotal) && !IsScalping)
            {
                Reset();
                return;
            }

            // Start Scalping - Start tracking the position
            // Improvement: Use the Consecutive Ticks Indicator.
            if (
                gammaTotal > 0 && !IsScalping ||
                ScalpingDirection != Num2Direction(deltaTotal)
                )
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
            _triggerPrice = 0;
            _startingPrice = 0;
        }

        private void SetNewTriggerPrices()
        {
            decimal deltaTotal = _algo.DeltaMV(Symbol);
            ScalpingDirection = Num2Direction(deltaTotal);
            _startingPrice = MidPrice;
            _triggerPrice = GetTriggerPrice();
            _algo.Log($"{_algo.Time} GammaScalper.SetNewTriggerPrices: Symbol={Symbol}, StartingPrice=MidPrice={MidPrice}, TriggerPrice={_triggerPrice}, delta={deltaTotal}, ScalpingDirection={ScalpingDirection}");
            if (ScalpingDirection == OrderDirection.Buy || ScalpingDirection == OrderDirection.Sell)
            {
                IsScalping = true;
            }            
        }

        private decimal GetTriggerPrice() {
            return ScalpingDirection switch
            {
                OrderDirection.Buy => MidPrice * (1 - _trailingPct),
                OrderDirection.Sell => MidPrice * (1 + _trailingPct),
                _ => 0
            }; ;
        }
        private decimal GetUpdatedTriggerPrice()
        {
            return ScalpingDirection switch
            {
                OrderDirection.Buy => Math.Max(_triggerPrice, MidPrice * (1 - _trailingPct)),
                OrderDirection.Sell => Math.Min(_triggerPrice, MidPrice * (1 + _trailingPct)),
                _ => 0
            };
        }
        private void UpdateTriggerPrices()
        {
            decimal deltaTotal = _algo.DeltaMV(Symbol);
            if (Num2Direction(deltaTotal) != ScalpingDirection)
            {
                _algo.Log($"{_algo.Time} GammaScalper.UpdateTriggerPrices: Delta sign has changed. Resetting scalper.");
                ReviewScalper(Symbol);
                return;
            }

            var newTriggerPrice = GetUpdatedTriggerPrice();
            if (newTriggerPrice != _triggerPrice)
            {
                _algo.Log($"{_algo.Time} GammaScalper.UpdateTriggerPrices: Symbol={Symbol}, MidPrice={MidPrice}, TriggerOld={_triggerPrice}, TriggerNew={newTriggerPrice}, StartingPrice={_startingPrice}, ScalpingGains={ScalpingGains()}");
                _triggerPrice = newTriggerPrice;
            }
        }

        private bool IsTriggered() => ScalpingDirection switch
        {
            OrderDirection.Buy => MidPrice <= _triggerPrice,
            OrderDirection.Sell => MidPrice >= _triggerPrice,
        };

        /// <summary>
        /// If range bound, frequent hedging of tiny delta moves can incur more transaction costs than the gamma scalping profits.
        /// Hence check, if delta deviation from zero is greater than expected fee, hedge, otherwise only hedge if band is exceeded.
        /// Todo: This is a hack. Should be done in a more principled way.
        /// </summary>
        private void Hedge()
        {
            bool deltaRiskExceedingBand = _algo.PfRisk.CheckHandleDeltaRiskExceedingBand(Symbol);
            if (deltaRiskExceedingBand)// || scalpingGainsExceedTransactionCosts)
            {
                bool scalpingGainsExceedTransactionCosts = DoScalpingGainsExceedTransactionCosts();
                _algo.Log($"{_algo.Time} GammaScalper.Hedge: Symbol={Symbol}, Delta={_algo.DeltaMV(Symbol)}, MidPrice={MidPrice}, TriggerPrice={_triggerPrice}, ScalpingDirection={ScalpingDirection}, " +
                    $"scalpingGainsExceedTransactionCosts={scalpingGainsExceedTransactionCosts}, deltaRiskExceedingBand={deltaRiskExceedingBand}");
                _algo.ExecuteHedge(Symbol, _algo.EquityHedgeQuantity(Symbol), OrderType.Market);  // Having this here is error prone and should be centralized. Switch from market to limit/updating in future easily forgotten.
            }
        }

        private decimal ScalpingGains()
        {
            decimal gammaTotal = _algo.PfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            decimal scalpedDelta = gammaTotal * (MidPrice - _startingPrice);
            return scalpedDelta * (MidPrice - _startingPrice);
        }

        private bool DoScalpingGainsExceedTransactionCosts()
        {
            decimal deltaTotal = _algo.DeltaMV(Symbol);

            decimal gammaTotal = _algo.PfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            decimal scalpedDelta = gammaTotal * (MidPrice - _startingPrice);            
            decimal gammaScalpingProfit = scalpedDelta * (MidPrice - _startingPrice);
            decimal transactionCosts = _algo.TransactionCosts(Symbol, scalpedDelta);
            _algo.Log($"{_algo.Time} GammaScalper.ScalpingGainsExceedTransactionCosts: Symbol={Symbol}, deltaTotal(notInCalc)={deltaTotal}, scalpedDelta={scalpedDelta}, gammaTotal={deltaTotal}, gammaScalpingProfit={gammaScalpingProfit}, transactionCosts={transactionCosts}");
            return gammaScalpingProfit > transactionCosts;
        }
        public string StatusShort()
        {
            return IsScalping ? $"GammaScalpingTriggerPrice={TriggerPrice}" : "IsGammaScalping=false";
        }
        public string Status()
        {
            return $"Symbol={Symbol}, IsScalping={IsScalping}, ScalpingDirection={ScalpingDirection}, TriggerPrice={_triggerPrice}, MidPrice={MidPrice}, StartingPrice={_startingPrice}, ScalpingGains={ScalpingGains()}";
        }
    }
}
