using QuantConnect.Securities.Option;
using System;

namespace QuantConnect.Algorithm.CSharp.Core.RealityModeling
{
    public class CustomOptionAssignmentModel : DefaultOptionAssignmentModel
    {
        public CustomOptionAssignmentModel(decimal requiredInTheMoneyPercent, TimeSpan? priorExpiration = null) : base(requiredInTheMoneyPercent, priorExpiration)
        {
        }
        //public override OptionAssignmentResult GetAssignment(OptionAssignmentParameters parameters)
        //{
        //    var result = base.GetAssignment(parameters);
        //    result.Tag = "Custom Option Assignment";
        //    return result;
        //}
    }
}
