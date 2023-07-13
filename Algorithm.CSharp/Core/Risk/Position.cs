using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class Position
    {
        public Symbol Symbol { get; }
        public Security Security { get; }
        public Symbol UnderlyingSymbol
        {
            get => SecurityType switch
            {
                SecurityType.Equity => Symbol,
                SecurityType.Option => ((Option)Security).Underlying.Symbol,
                _ => throw new NotSupportedException()
            };
        }
        public DateTime Since { get; set; }
        public DateTime TimeCreated { get; }

        public SecurityType SecurityType { get; }
        public virtual decimal Quantity { get => Holding.Quantity; }
        public int Multiplier { get => SecurityType == SecurityType.Option ? 100 : 1; }
        public SecurityHolding Holding { get; }
        public virtual DateTime Ts1 { get => algo.Time; }
        public virtual decimal P1 { get => algo.MidPrice(Symbol); }
        public virtual decimal Bid1 { get => algo.Securities[Symbol].BidPrice; }
        public virtual decimal Ask1 { get => algo.Securities[Symbol].AskPrice; }
        public decimal Mid1 { get { return (Bid1 + Ask1) / 2; } }
        public virtual double IVBid1
        {
            get => SecurityType switch
            {
                SecurityType.Option => algo.RollingIVBid[Symbol].Current,
                _ => 0
            };
        }
        public virtual double IVAsk1
        {
            get => SecurityType switch
            {
                SecurityType.Option => algo.RollingIVAsk[Symbol].Current,
                _ => 0
            };
        }
        public double IVMid1 { get => (IVBid1 + IVAsk1) / 2; }
        public decimal Mid1Underlying { get => (Bid1Underlying + Ask1Underlying) / 2; }
        public virtual decimal Bid1Underlying { get => algo.Securities[UnderlyingSymbol].BidPrice; }
        public virtual decimal Ask1Underlying { get => algo.Securities[UnderlyingSymbol].AskPrice; }
        public decimal UnrealizedProfit { get => Holding.UnrealizedProfit; }

        private GreeksPlus greeks1;
        public GreeksPlus Greeks1 { get => GetGreeks1(); }
        public decimal ValueMid { get { return Mid1 * Quantity * Multiplier; } }
        public decimal ValueWorst { get { return (Quantity > 0 ? Bid1 : Ask1) * Quantity * Multiplier; } }  // Bid1 presumably defaults to zero. For Ask1, infinite loss for short call.
        public decimal ValueClose { get { return algo.Securities[Symbol].Close * Quantity * Multiplier; } }        

        private readonly Foundations algo;

        public Position(Foundations algo, SecurityHolding holding)
        {
            this.algo = algo;
            Holding = holding;
            Symbol = holding.Symbol;
            Security = algo.Securities[Symbol];
            TimeCreated = algo.Time;
            SecurityType = Security.Type;
            
        }

        private GreeksPlus GetGreeks()
        {
            return SecurityType switch
            {
                SecurityType.Option => new GreeksPlus(OptionContractWrap.E(algo, (Option)Security)),
                SecurityType.Equity => new GreeksPlus(),
                _ => throw new NotSupportedException()
            };
        }

        public double Delta(bool implied = false, double? volatility = null)
        {
            switch (SecurityType)
            {
                case SecurityType.Equity:
                    return 1;
                case SecurityType.Option:
                    var greeks = Greeks1;
                    if (implied)
                    {
                        greeks.OCW.SetIndependents(volatility: volatility ?? IVMid1);
                    }
                    try
                    {
                        return greeks.Delta;
                    }
                    catch
                    {
                        greeks.OCW.SetIndependents();
                        return greeks.Delta;
                    }

                default:
                    throw new NotSupportedException();
            }
        }

        public decimal TaylorTerm()
        {
            // delta/gamma gamma is unitless sensitivity. No scaling here.
            Option contract = (Option)Security;
            return contract.ContractMultiplier * Quantity * Mid1Underlying;
        }

        public decimal DeltaTotal(bool implied = false, double? volatility = null)
        {
            return SecurityType switch
            {
                SecurityType.Equity => (decimal)Delta(implied, volatility: volatility) * Quantity,
                SecurityType.Option => (decimal)Delta(implied, volatility: volatility) * ((Option)Security).ContractMultiplier * Quantity
            };
        }

        public decimal Delta100BpUSD(bool implied = false, double? volatility = null)
        {
            return SecurityType switch
            {
                // Scaled price into a 1% change / 100BP. That changes times delta and position is risk of position moving by 1%. That's a hundreth of IB's 'Delta Dollar' metric.
                SecurityType.Equity => (decimal)Delta(implied, volatility: volatility) * Quantity * P1 * 100 * BP,
                SecurityType.Option => (decimal)Delta(implied, volatility: volatility) * TaylorTerm() * 100 * BP
            };
        }

        public double Gamma()
        {
            return Greeks1.Gamma;
        }
        public decimal Gamma100BpUSD
        {
            get => SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)(0.5 * Math.Pow((double)TaylorTerm(), 2) * Gamma() * Math.Pow((double)(100 * BP), 2))
            };
        }

        public decimal GammaTotal
        {
            get => SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Gamma() * ((Option)Security).ContractMultiplier * Quantity
            };
        }

        // Below 2 simplifications assuming a pure options portfolio.
        public double Theta()
        {
            return Greeks1.Theta;
        }

        public decimal ThetaTotal
        {
            get => SecurityType switch
            {
                SecurityType.Equity => (decimal)Theta() * Quantity,
                SecurityType.Option => (decimal)Theta() * ((Option)Security).ContractMultiplier * Quantity
            };
        }
        public decimal Theta1DayUSD()
        {
            return SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks1.Theta * TaylorTerm() * 100 * BP
            };
        }

        // Summing up individual vegas. Only applicable to Ppi constructed from options, not for Ppi(SPY or any index)
        public double Vega()
        {
            return Greeks1.Vega;
        }
        public decimal Vega100BpUSD
        {
            get => SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks1.Vega * TaylorTerm() * 100 * BP
            };
        }

        public decimal VegaTotal
        {
            get => SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks1.Vega * ((Option)Security).ContractMultiplier * Quantity
            };
        }
        private GreeksPlus GetGreeks1()
        {
            greeks1 ??= GetGreeks();
            if (SecurityType == SecurityType.Option)
            {
                greeks1.OCW.SetIndependents(Mid1Underlying, volatility: (double)greeks1.OCW.HistoricalVolatility());  // to be revised. we are now interested in implied Greeks.
            }
            return greeks1;
        }
    }
}
