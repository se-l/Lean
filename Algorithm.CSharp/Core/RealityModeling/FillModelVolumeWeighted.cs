using NodaTime;
using QuantConnect.Orders;
using System;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.RealityModeling
{
    /// <summary>
    /// Problems with the immediate fill model:
    /// Option algos on second resolution experience many more fills during live trading when quotes are NBBO or better. That is why this fill model assigns a probabiliy of getting filled based on average daily volume, time of day and how much spread is crossed.
    /// The immedatiate fill model becomes a first stage check. This probabilistic model assigns additional fills.
    /// Rather need intraday and intraspread fill distribution.
    /// 
    /// Current formulas dont assign a higher chance to high-volume contracts currently....
    /// Getting too often filled for large tenors.
    /// </summary>
    public class FillModelVolumeWeighted : FillModelMine
    {
        private readonly decimal MeanDailyVolume;
        private readonly Random random;
        private readonly ILiquiditySurface liquiditySurface;
        private readonly double VolWeight;
        private readonly double PFillSpreadExp;
        private readonly double LambdaBase;
        private readonly double VolumeExponent;
        private readonly double MinVolumeWeight;
        private readonly double MaxVolumeWeight;
        // Some convexity adjustment. More fills at the start of the day.
        public FillModelVolumeWeighted(
            decimal meanDailyVolume,
            int baseVolumeWeight,
            double pFillSpreadExp,
            ILiquiditySurface liquiditySurface,
            int? randomSeed = null,
            double lambdaBase = 0.35,
            double volumeExponent = 0.5,
            double minVolumeWeight = 0.25,
            double maxVolumeWeight = 4.0)
        {
            MeanDailyVolume = meanDailyVolume;
            var baseWeight = Math.Max(1, baseVolumeWeight);
            VolWeight = (double)meanDailyVolume / baseWeight;
            PFillSpreadExp = pFillSpreadExp;
            this.liquiditySurface = liquiditySurface ?? new StaticLiquiditySurface();
            random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
            LambdaBase = lambdaBase;
            VolumeExponent = volumeExponent;
            MinVolumeWeight = minVolumeWeight;
            MaxVolumeWeight = maxVolumeWeight;
        }

        /// <summary>
        /// Default limit order fill model in the base security class.
        /// </summary>
        protected override OrderEvent InternalLimitFill(Security asset, Order order, decimal limitPrice, decimal quantity)
        {
            OrderEvent fill = base.InternalLimitFill(asset, order, limitPrice, quantity);
            if (fill.Status == OrderStatus.Filled) return fill;

            decimal spread = asset.AskPrice - asset.BidPrice;
            if (spread <= 0)
            {
                return fill;
            }

            var rawSpreadCrossedPc = (double)(order.Direction == OrderDirection.Buy
                ? (limitPrice - asset.BidPrice) / spread
                : (asset.AskPrice - limitPrice) / spread);
            double spreadCrossedPc = Math.Clamp(rawSpreadCrossedPc, 0d, 1d);
            if (spreadCrossedPc <= 0)
            {
                return fill;
            }

            double spreadTerm = Math.Pow(spreadCrossedPc, Math.Max(0d, PFillSpreadExp));
            double volumeTerm = Math.Clamp(Math.Pow(Math.Max(0d, VolWeight), VolumeExponent), MinVolumeWeight, MaxVolumeWeight);
            double liquidityWeight = Math.Max(0d, liquiditySurface.GetLiquidityWeight(asset, fill.UtcTime));
            double lambda = LambdaBase * spreadTerm * volumeTerm * liquidityWeight;
            double pFill = 1d - Math.Exp(-Math.Max(0d, lambda));

            if (random.NextDouble() < pFill)
            {
                DateTime localTime = fill.UtcTime.ConvertTo(DateTimeZone.Utc, asset.Exchange.TimeZone);
                Logging.Log.Trace($"{localTime} FillModelVolumeWeighted: Filled quantity={quantity}, symbol={order.Symbol}, " +
                    $"bid={asset.BidPrice}, fillPrice={limitPrice}, ask={asset.AskPrice}, spreadCrossedPc={spreadCrossedPc:0.00},  " +
                    $"pFill={pFill:0.00}, spreadTerm={spreadTerm:0.00}, volumeTerm={volumeTerm:0.00}, liqWeight={liquidityWeight:0.00}, MeanDailyVolume ={MeanDailyVolume:0.0}");

                fill.Status = OrderStatus.Filled;
                fill.FillPrice = limitPrice;
                fill.FillQuantity = quantity;
            }

            return fill;
        }
    }
}
