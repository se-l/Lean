using QuantConnect.Orders;
using System;
using QuantConnect.Securities;

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
        private readonly Random random = new (1);
        private readonly double VolWeight;
        private readonly double PFillSpreadExp;
        // Some convexity adjustment. More fills at the start of the day.
        public FillModelVolumeWeighted(decimal meanDailyVolume, int baseVolumeWeight, double pFillSpreadExp) 
        {
            MeanDailyVolume = meanDailyVolume;
            VolWeight = (double)meanDailyVolume / baseVolumeWeight;
            PFillSpreadExp = pFillSpreadExp;
        }

        /// <summary>
        /// Default limit order fill model in the base security class.
        /// </summary>
        protected override OrderEvent InternalLimitFill(Security asset, Order order, decimal limitPrice, decimal quantity)
        {
            OrderEvent fill = base.InternalLimitFill(asset, order, limitPrice, quantity);
            if (fill.Status == OrderStatus.Filled) return fill;

            decimal spread = asset.AskPrice - asset.BidPrice;
            double spreadCrossedPc = (double)(order.Direction == OrderDirection.Buy ? (limitPrice - asset.BidPrice) / spread : (asset.AskPrice - limitPrice) / spread);
            double pFillSpread = spreadCrossedPc * Math.Pow(spreadCrossedPc, PFillSpreadExp);
            double pFill = pFillSpread * VolWeight;

            if (random.NextDouble() < pFill)
            {
                Logging.Log.Trace($"UTC {fill.UtcTime} FillModelVolumeWeighted: Filled quantity={quantity}, symbol={order.Symbol}, " +
                    $"bid={asset.BidPrice}, fillPrice={limitPrice}, ask={asset.AskPrice}, spreadCrossedPc={spreadCrossedPc},  " +
                    $"pFill={pFill}, pFillSpread={pFillSpread}, MeanDailyVolume ={MeanDailyVolume}");

                fill.Status = OrderStatus.Filled;
                fill.FillPrice = limitPrice;
                fill.FillQuantity = quantity;
            }

            return fill;
        }
    }
}
