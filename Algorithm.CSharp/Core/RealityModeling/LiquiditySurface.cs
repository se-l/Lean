using System;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.RealityModeling
{
    public interface ILiquiditySurface
    {
        double GetLiquidityWeight(Security optionSecurity, DateTime utcTime);
    }

    public class StaticLiquiditySurface : ILiquiditySurface
    {
        public double GetLiquidityWeight(Security optionSecurity, DateTime utcTime)
        {
            if (optionSecurity == null || optionSecurity.Type != SecurityType.Option)
            {
                return 1d;
            }

            var option = optionSecurity as Option;
            if (option == null)
            {
                return 1d;
            }

            var underlyingPrice = option.Underlying?.Price ?? option.Underlying?.Close ?? 0m;
            if (underlyingPrice <= 0m)
            {
                return 1d;
            }

            var strike = optionSecurity.Symbol.ID.StrikePrice;
            if (strike <= 0m)
            {
                return 1d;
            }

            double logMoneyness = Math.Abs(Math.Log((double)(strike / underlyingPrice)));
            int dte = Math.Max(0, (optionSecurity.Symbol.ID.Date.Date - utcTime.Date).Days);

            double moneynessWeight = logMoneyness switch
            {
                <= 0.01 => 1.60,
                <= 0.03 => 1.35,
                <= 0.06 => 1.10,
                <= 0.10 => 0.85,
                <= 0.18 => 0.65,
                _ => 0.45
            };

            double tenorWeight = dte switch
            {
                <= 7 => 1.40,
                <= 21 => 1.25,
                <= 45 => 1.05,
                <= 90 => 0.85,
                <= 180 => 0.65,
                _ => 0.45
            };

            return Math.Clamp(moneynessWeight * tenorWeight, 0.25, 2.50);
        }
    }
}
