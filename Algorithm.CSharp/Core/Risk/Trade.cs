using System;
using System.Collections.Generic;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class Trade
    {
        public Symbol Symbol { get; }
        public Security Security { get; }
        public Symbol UnderlyingSymbol { get; }
        public DateTime Since { get; set; }
        public DateTime TimeCreated { get; }
        public DateTime LastUpdated { get; }

        public SecurityType SecurityType { get; }
        public decimal Quantity { get; }
        public int Multiplier { get; }
        public Order Order { get; }
        public decimal Spread { get; }

        public decimal PriceFillAvg { get; }
        public DateTime FirstFillTime { get; }
        public DateTime Ts0 { get; set; }
        public decimal P0 { get; set; }
        public decimal Bid0 { get; set; }
        public decimal Ask0 { get; set; }
        public decimal Mid0 { get { return (Bid0 + Ask0) / 2; } }
        public DateTime Ts1 { get; set; }
        public decimal P1 { get; set; }
        public decimal Bid1 { get; set; }
        public decimal Ask1 { get; set; }
        public decimal Mid1 { get { return (Bid1 + Ask1) / 2; } }
        public decimal DP { get; set; }

        public decimal Mid0Underlying { get; set; } = 0;
        public decimal Bid0Underlying { get; set; } = 0;
        public decimal Ask0Underlying { get; set; } = 0;
        public decimal Mid1Underlying { get; set; } = 0;
        public decimal Bid1Underlying { get; set; } = 0;
        public decimal Ask1Underlying { get; set; } = 0;
        public decimal DPUnderlying { get; set; } = 0;


        public decimal PL { get; set; } = 0;
        public decimal UnrealizedProfit { get; set; } = 0;
        public PLExplain PLExplain { get { return GetPLExplain(); } }
        public Dictionary<Symbol, double> BetaUnderlying { get;  } = new(); 
        public Dictionary<Symbol, double> Correlations { get;  } = new();

        private GreeksPlus greeks;
        public double DeltaSPY { get; }
        public decimal DeltaSPY100BpUSD { get; }

        public decimal ValueMid { get { return Mid1 * Quantity * Multiplier; } }
        public decimal ValueWorst { get { return (Quantity != 0 ? Bid1 : Ask1) * Quantity * Multiplier; } }  // Bid1 presumably defaults to zero. For Ask1, infinite loss for short call.
        public decimal ValueClose { get { return algo.Securities[Symbol].Close * Quantity * Multiplier; } }


        private readonly Foundations algo;

        public Trade(Foundations algo, Order order)
        {
            this.algo  = algo;
            Order = order;

            Symbol = Order.Symbol;           
            Security = algo.Securities[Symbol];
            TimeCreated = algo.Time;
            Symbol = Symbol;
            SecurityType = Security.Type;
            Quantity = algo.Portfolio[Symbol].Quantity;

            var bestBid = Security.BidPrice;
            var bestAsk = Security.AskPrice;
            Spread = bestAsk - bestBid;
            Multiplier = SecurityType == SecurityType.Option ? 100 : 1;

            P1 = algo.MidPrice(Symbol);
            Ts1 = algo.Time;
            Bid1 = algo.Securities[Symbol].BidPrice;
            Ask1 = algo.Securities[Symbol].AskPrice;


            decimal HoldingsQuantitySince = Order.Quantity * Multiplier;
            PL = HoldingsQuantitySince * P1;  // MTM Current HoldingsValue.
            PL -= Order.Value * Multiplier;  // Amounts paid received to arrive at the current Position.
            // Consider breaking down into Bid/Ask - worst scenario should tally with QC/IB.
            LastUpdated = (DateTime)(Order.LastFillTime ?? TimeCreated);
            PriceFillAvg = Order.Price;
            FirstFillTime = (DateTime)(Order.LastFillTime ?? TimeCreated);
            FirstFillTime = FirstFillTime.ConvertFromUtc(algo.TimeZone);
            Ts0 = FirstFillTime;
            P0 = PriceFillAvg;
            Bid0 = Order?.OrderSubmissionData?.BidPrice ?? 0;
            Ask0 = Order?.OrderSubmissionData?.AskPrice ?? 0;
            DP = P1 - PriceFillAvg;

            // Option specific
            if (SecurityType == SecurityType.Option)
            {
                Option option = ((Option)Security);
                UnderlyingSymbol = option.Underlying.Symbol;

                Mid1Underlying = algo.MidPrice(UnderlyingSymbol);
                Bid1Underlying = algo.Securities[UnderlyingSymbol].BidPrice;
                Ask1Underlying = algo.Securities[UnderlyingSymbol].AskPrice;

                //var ocw = OptionContractWrap.E(algo, option);
                //Greeks = ocw.Greeks(null, null);
                //PLExplain = new PLExplain(Greeks, (double)DPUnderlying, businessDays, 0, 0);
                BetaUnderlying[algo.spy] = algo.Beta(algo.spy, UnderlyingSymbol, 20, Resolution.Daily);
                Correlations[algo.spy] = algo.Correlation(algo.spy, UnderlyingSymbol, 20, Resolution.Daily);

                DeltaSPY = Greeks().Delta * BetaUnderlying[algo.spy] * (double)Mid1Underlying / (double)algo.MidPrice(algo.spy);
                DeltaSPY100BpUSD = (decimal)DeltaSPY * option.ContractMultiplier * Quantity * algo.MidPrice(algo.spy);
            }
            else
            {
                UnderlyingSymbol = Symbol;
                BetaUnderlying[algo.spy] = algo.Beta(algo.spy, Symbol, 20, Resolution.Daily);
                Correlations[algo.spy] = algo.Correlation(algo.spy, Symbol, 20, Resolution.Daily);

                DeltaSPY = BetaUnderlying[algo.spy] * (double)P1 / (double)algo.MidPrice(algo.spy);
                DeltaSPY100BpUSD = (decimal)DeltaSPY * Quantity * algo.MidPrice(algo.spy);
            }

            // PNL
            UnrealizedProfit = Security.Price - PriceFillAvg * Quantity * Multiplier;
            if (SecurityType == SecurityType.Option)
            {
                Bid0Underlying = Order?.OrderSubmissionDataUnderlying?.BidPrice ?? 0;
                Ask0Underlying = Order?.OrderSubmissionDataUnderlying?.AskPrice ?? 0;
                Mid0Underlying = (Bid0Underlying + Ask0Underlying) / 2;
                DPUnderlying = Mid1Underlying - Mid0Underlying;
            }
        }
        public GreeksPlus Greeks()
        {
            if (greeks == null && SecurityType == SecurityType.Option)
            {
                OptionContractWrap ocw = OptionContractWrap.E(algo, (Option)Security);
                greeks = new GreeksPlus(ocw);
            }
            else if (greeks == null && SecurityType == SecurityType.Equity)
            {
                greeks = new GreeksPlus();
            }
            return greeks;
        }

        public double PfDelta()
        {
            return SecurityType switch
            {
                SecurityType.Equity => 1,
                SecurityType.Option => Greeks().Delta
            };
        }

        public decimal TaylorTerm()
        {
            // 100 * BP is already the unit of delta/gamma. A 1% change in the underlying... -> will always yield 1 for options.
            Option contract = (Option)Security;
            return contract.ContractMultiplier * Quantity * Mid1Underlying;
        }

        public decimal PfDelta100BpUSD()
        {

            return SecurityType switch
            {
                SecurityType.Equity => (decimal)PfDelta() * Quantity * P1,
                SecurityType.Option => (decimal)PfDelta() * TaylorTerm()
            };
        }

        public double PfGamma()
        {
            return Greeks().Gamma;
        }
        public decimal PfGamma100BpUSD() 
        {
            return SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)(0.5 * Math.Pow((double)TaylorTerm(), 2) * PfGamma())
            };
        }

        // Below 2 simplifications assuming a pure options portfolio.
        public double PfTheta() 
        {
            return Greeks().Theta;
        }
        public decimal PfThetaUSD() 
        {
            return SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks().Theta * (Security as Option).ContractMultiplier * Quantity
            };
        }

        // Summing up individual vegas. Only applicable to Ppi constructed from options, not for Ppi(SPY or any index)
        public double PfVega() 
        {
            return Greeks().Vega;
        }
        public decimal PfVega100BpUSD() 
        {
            return SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks().Vega * (Security as Option).ContractMultiplier * Quantity  // * 100 * BP * algo.Securities[ocw.UnderlyingSymbol].VolatilityModel.Volatility;
            };
        }

        private PLExplain GetPLExplain()
        {
            //return SecurityType == SecurityType.Option ? new PLExplain(Greeks, (double)DPUnderlying, GetBusinessDays(Since, algo.Time), 0, 0) : null;
            if (SecurityType == SecurityType.Option)
            {
                return new PLExplain(
                    Greeks(),
                    (double)(DPUnderlying * Quantity * Multiplier),
                    1,
                    0,  // IV
                    0);
                    }
            else
            {
                return new PLExplain(Greeks(), dP: (double)DP, dT: 1, 0, 0, position: (double)Quantity * Multiplier);
            }
        }

        public string ToCSV(IEnumerable<Trade>? trades = null, bool header = false)
        {
            return "";
        }
    }
}
