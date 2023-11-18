using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp.Core.RealityModeling
{
    public class BuyingPowerModelMine : BuyingPowerModel
    {
        private readonly Foundations _algo;
        private decimal _initialMarginRequirement;
        private decimal _maintenanceMarginRequirement;
        private decimal _qcMarginAdjustmentFactor;

        public BuyingPowerModelMine(Foundations algo, decimal qcMarginAdjustmentFactor = 0.5m)
        {
            _algo = algo;
            _qcMarginAdjustmentFactor = qcMarginAdjustmentFactor;
        }

        /// <summary>
        /// Gets the margin currently allocated to the specified holding
        /// </summary>
        /// <param name="parameters">An object containing the security and holdings quantity/cost/value</param>
        /// <returns>The maintenance margin required for the provided holdings quantity/cost/value</returns>
        public override MaintenanceMargin GetMaintenanceMargin(MaintenanceMarginParameters parameters)
        {
            if (_algo.LiveMode)
            {
                return _algo.Portfolio.MarginMetrics.FullMaintMarginReq;
            }
            else
            {
                return parameters.AbsoluteHoldingsValue * _maintenanceMarginRequirement * _qcMarginAdjustmentFactor;
            }
        }

        /// <summary>
        /// The margin that must be held in order to increase the position by the provided quantity
        /// </summary>
        /// <param name="parameters">An object containing the security and quantity</param>
        /// <returns>The initial margin required for the provided security and quantity</returns>
        public override InitialMargin GetInitialMarginRequirement(InitialMarginParameters parameters)
        {
            if (_algo.LiveMode)
            {
                return _algo.Portfolio.MarginMetrics.FullInitMarginReq;
            }
            else
            {
                var security = parameters.Security;
                var quantity = parameters.Quantity;
                return security.QuoteCurrency.ConversionRate
                    * security.SymbolProperties.ContractMultiplier
                    * security.Price
                    * quantity
                    * _initialMarginRequirement
                    * _qcMarginAdjustmentFactor;
            }            
        }
    }
}
