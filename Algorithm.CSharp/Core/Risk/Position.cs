using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Indicators;
using System.Diagnostics.Contracts;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class Position
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
        public decimal Spread { get; }

        public decimal PriceMid { get; }
        public DateTime Ts0 { get; set; }
        public decimal P0 { get; set; }
        public decimal Bid0 { get; set; }
        public decimal Ask0 { get; set; }
        public DateTime Ts1 { get; set; }
        public decimal P1 { get; set; }
        public decimal Bid1 { get; set; }
        public decimal Ask1 { get; set; }
        public decimal DP { get; set; }

        public decimal P0Underlying { get; set; } = 0;
        public decimal Bid0Underlying { get; set; } = 0;
        public decimal Ask0Underlying { get; set; } = 0;
        public decimal P1Underlying { get; set; } = 0;
        public decimal Bid1Underlying { get; set; } = 0;
        public decimal Ask1Underlying { get; set; } = 0;
        public decimal DPUnderlying { get; set; } = 0;


        public decimal PL { get; }
        public GreeksPlus Greeks { get { return GetGreeksPlus(); } }  // expensive, hence not initialized in constructor.
        public PLExplain PLExplain { get { return GetPLExplain(); } }
        public Dictionary<Symbol, double> BetaUnderlying { get;  } = new();  // not working as CSV output. Need more. Like an Info object holding name key. 
        // And generally refactor the CSV output to concatenate the names of nested objects...
        public Dictionary<Symbol, double> Correlations { get;  } = new();

        //public GreeksPlus PfGreeks;
        public double PfDelta { get; set; }
        public decimal PfDelta100BpUSD { get; set; }
        public double PfGamma { get; set; }
        public decimal PfGamma100BpUSD { get; set; }
        public double PfTheta { get; set; }
        public decimal PfThetaUSD { get; set; }
        public double PfVega { get; set; }
        public decimal PfVega100BpUSD { get; set; }
        public double DeltaSPY { get; }
        public decimal DeltaSPY100BpUSD { get; }


        private readonly Foundations algo;

        public Position(Foundations algo, Symbol symbol)
        {
            this.algo  = algo;

            Symbol = symbol;           
            Security = algo.Securities[symbol];
            TimeCreated = algo.Time;
            Symbol = symbol;
            SecurityType = Security.Type;
            Quantity = algo.Portfolio[symbol].Quantity;

            PriceMid = algo.MidPrice(symbol);

            var bestBid = Security.BidPrice;
            var bestAsk = Security.AskPrice;
            Spread = bestAsk - bestBid;
            Multiplier = SecurityType == SecurityType.Option ? 100 : 1;

            P1 = algo.MidPrice(symbol);
            Ts1 = algo.Time;
            Bid1 = algo.Securities[symbol].BidPrice;
            Ask1 = algo.Securities[symbol].AskPrice;

            // Accumulate HoldingsValue since.
            IEnumerable<Order> filledOrdersSince = algo.Transactions.GetOrders(x => x.Symbol == symbol && x.Value > 0 && x.CreatedTime >= Since);            
            decimal HoldingsQuantitySince = filledOrdersSince.Sum(o => o.Quantity);
            PL = HoldingsQuantitySince * P1;  // MTM Current HoldingsValue.
            PL += filledOrdersSince.Sum(o => o.Value);  // Amounts paid received to arrive at the current Position.
            // Consider breaking down into Bid/Ask - worst scenario should tally with QC/IB.
            LastUpdated = (DateTime)(!filledOrdersSince.Any() ? TimeCreated : filledOrdersSince.Last().LastFillTime);

            // Option specific
            if (SecurityType == SecurityType.Option)
            {
                Option option = ((Option)Security);
                UnderlyingSymbol = option.Underlying.Symbol;

                P1Underlying = algo.MidPrice(UnderlyingSymbol);
                Bid1Underlying = algo.Securities[UnderlyingSymbol].BidPrice;
                Ask1Underlying = algo.Securities[UnderlyingSymbol].AskPrice;

                //var ocw = OptionContractWrap.E(algo, option);
                //Greeks = ocw.Greeks(null, null);
                //PLExplain = new PLExplain(Greeks, (double)DPUnderlying, businessDays, 0, 0);
                BetaUnderlying[algo.spy] = algo.Beta(algo.spy, UnderlyingSymbol, 30, Resolution.Daily);
                Correlations[algo.spy] = algo.Correlation(algo.spy, UnderlyingSymbol, 30, Resolution.Daily);

                DeltaSPY = Greeks.Delta * BetaUnderlying[algo.spy] * (double)P1Underlying / (double)algo.MidPrice(algo.spy);
                DeltaSPY100BpUSD = (decimal)DeltaSPY * option.ContractMultiplier * Quantity * algo.MidPrice(algo.spy);
            }
            else
            {
                UnderlyingSymbol = symbol;
                BetaUnderlying[algo.spy] = algo.Beta(algo.spy, Symbol, 30, Resolution.Daily);
                Correlations[algo.spy] = algo.Correlation(algo.spy, Symbol, 30, Resolution.Daily);

                DeltaSPY = BetaUnderlying[algo.spy] * (double)P1 / (double)algo.MidPrice(algo.spy);
                DeltaSPY100BpUSD = (decimal)DeltaSPY * Quantity * algo.MidPrice(algo.spy);
            }
            SetPfGreeks(); // Likely useless, to be deleted once removed from PortfolioRisk.
        }

        public void SetPLSince(DateTime? since = null)
        {
            // Likely needs to go into a PL class... That would be pushed into a PL Explain class....
            Since = since ?? DateTime.Now.AddDays(-99);
            int businessDays = GetBusinessDays(Since, algo.Time);
            businessDays = businessDays == 0 ? 1 : businessDays;

            try
            {
                var bar = algo.HistoryWrapQuote(Symbol, businessDays, Resolution.Daily).First();
                Bid0 = bar.Bid.Close;
                Ask0 = bar.Ask.Close;
                P0 = MidPrice(bar);
                Ts0 = bar.EndTime;
            }
            catch
            {
                var bars = algo.History(Symbol, businessDays, Resolution.Daily);
                if (!bars.Any())
                {
                    algo.Log($"Failed to fetch data for {Symbol} {businessDays}");
                }
                var bar = bars.First();
                P0 = bar.Close;
                Ts0 = bar.EndTime;
            }
            DP = P1 - P0;

            if (SecurityType == SecurityType.Option)
            {
                try
                {
                    // Cannot succeed presently as there is no Daily Quote data for Underlying, only Minute. Would need to cache 
                    // another signature.
                    QuoteBar bar = algo.HistoryWrapQuote(UnderlyingSymbol, businessDays, Resolution.Daily).First();
                    P0Underlying = MidPrice(bar);
                    Bid0Underlying = bar.Bid.Close;
                    Ask0Underlying = bar.Ask.Close;
                }
                catch
                {
                    P0Underlying = algo.HistoryWrap(UnderlyingSymbol, businessDays, Resolution.Daily).First().Close;
                }
                DPUnderlying = P1Underlying - P0Underlying;
            }
        }

        private GreeksPlus GetGreeksPlus() {
            if (SecurityType == SecurityType.Option)
            {
                Option option = (Option)algo.Securities[Symbol];
                var ocw = OptionContractWrap.E(algo, option);
                return ocw.Greeks(null, null);
            }
            return null;
        }

        private PLExplain GetPLExplain()
        {
            return SecurityType == SecurityType.Option ? new PLExplain(Greeks, (double)DPUnderlying, GetBusinessDays(Since, algo.Time), 0, 0) : null;
        }

        public void SetPfGreeks()
        {
            //if (ppi == null) {  return ;}

            //decimal ppiPrice = ppi.Value();
            if (SecurityType == SecurityType.Equity)
            {
                //Equity equity = (Equity)algo.Securities[Symbol];
                double deltaSecurity = 1; //ppi.Delta(equity);  // Includes quantity/position.

                PfDelta = deltaSecurity;
                PfDelta100BpUSD = (decimal)deltaSecurity * Quantity * P1; // algo.MidPrice(Symbol);
            }
            else if (SecurityType == SecurityType.Option)
            {
                Option contract = (Option)Security;
                if (contract != null)
                {
                    //decimal price = algo.MidPrice(securityHolding.Symbol);
                    OptionContractWrap ocw = OptionContractWrap.E(algo, contract);
                    GreeksPlus greeks = ocw.Greeks(null, null);  // refactor any PF greeks there providing an index?

                    //double deltaPf = ppi.Delta(contract);  // Not including quantity or option multiplier.
                    double deltaPf = greeks.Delta;  // Not including quantity or option multiplier.
                    PfDelta = deltaPf;

                    // 100 * BP is already the unit of delta/gamma. A 1% change in the underlying... -> will always yield 1 for options.
                    var taylorTerm = contract.ContractMultiplier * Quantity * P1Underlying;
                    PfDelta100BpUSD = (decimal)deltaPf * taylorTerm;

                    //double gammaContract = ppi.Gamma(contract);  // Not including Quantity or option multiplier.
                    double gammaContract = greeks.Gamma;
                    PfGamma = gammaContract;
                    PfGamma100BpUSD = (decimal)(0.5 * Math.Pow((double)taylorTerm, 2) * gammaContract);

                    // Below 2 simplifications assuming a pure options portfolio.
                    PfTheta = greeks.Theta;
                    PfThetaUSD = (decimal)greeks.Theta * contract.ContractMultiplier * Quantity;

                    // Summing up individual vegas. Only applicable to Ppi constructed from options, not for Ppi(SPY or any index)
                    PfVega = greeks.Vega;
                    PfVega100BpUSD = (decimal)greeks.Vega * contract.ContractMultiplier * Quantity;  // * 100 * BP * algo.Securities[ocw.UnderlyingSymbol].VolatilityModel.Volatility;
                }
            }
        }
    }
}
