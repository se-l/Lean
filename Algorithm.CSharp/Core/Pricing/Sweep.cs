using QuantConnect.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    
    /// <summary>
    /// Requirement(# prio):
	/// - (1) Execute n contracts over sec seconds or within scheduled timeframes.
	/// - (2) Bin contracts by criteria: i.e., increases/decreases abs pf delta.
	/// - (5) Functional Sweep speed.e.g., f(liquidity)
	/// - Given the q constraint, would want to perform many sweeps throughout the day to fill all n contracts.Need a better risk limit DeltaUSDForStandardVolaMoveLike1%
	/// - Sweep high util orders more aggressively than low util.
	/// - Constraints:
	/// 	○ (2) Total delta and gamma within limits
	/// 	○ (4) Fill only q quantity at a time..To avoid exceeding delta limits(here optimize, can do more than 1 if total delta/gamma becomes better than before)
	/// 	○ (5) Functional Max sweep, e.g., half of USD util
	/// - Seeing tenor ~0.5 options with 0.20$ spread.Should get filled!!!
    /// </summary>
    public class Sweep  // ExecutionStrategy
    {
        public Symbol Symbol { get; internal set; }
        
        public OrderDirection Direction { get; internal set; }
        public decimal SweepRatio { get => _sweepRatio; }
        private bool _isSweeping;
        public bool IsSweeping => _isSweeping;

        //public event EventHandler<Sweep> UpdatedSweepRatio;

        private DateTime _sweepStartTime;

        private readonly Foundations _algo;
        private decimal _sweepRatio;
        public Sweep(Foundations algo, Symbol symbol, OrderDirection direction)
        {
            _algo = algo;
            Symbol = symbol;
            Direction = direction;
            //Underlying = Underlying(symbol);
            _sweepStartTime = _algo.Time;
            _isSweeping = false;
        }

        

        public void StartSweep()
        {
            _isSweeping = true;
            _sweepStartTime = _algo.Time;
        }

        public void PauseSweep()
        {
            _isSweeping = false;
        }

        public void ContinueSweep()
        {
            if (!_isSweeping) StartSweep();
            _isSweeping = true;
        }

        public void StopSweep()
        {
            _isSweeping = false;
            _sweepStartTime = DateTime.MaxValue;
        }

        //public void UpdateSweepRatio()
        //{
        //    if (_isSweeping)
        //    {
        //        _sweepRatio = SweetRatioByDuration;
        //    }
        //}

        //public void UpdateSweepRatio(OrderTicket ticket)
        //{
        //    if (ticket == null || !_isSweeping) return;

        //    decimal newRatio = SweetRatioByDuration;
        //    decimal limitPrice = ticket.Get(OrderField.LimitPrice);
        //    decimal bid = _algo.Securities[Symbol].BidPrice;
        //    decimal ask = _algo.Securities[Symbol].AskPrice;
        //    decimal spread = ask - bid;
        //    decimal newLimitPrice = ticket.Quantity > 0 ? bid + spread * newRatio : ask - spread * newRatio;
        //    _sweepRatio = newRatio;

        //    if (limitPrice != newLimitPrice)
        //    {
        //        //_algo.Log($"{_algo.Time} Sweep. Emit UpdatedSweepRatio event.");
        //        UpdatedSweepRatio?.Invoke(this, this);
        //    }
        //}
    }
}
