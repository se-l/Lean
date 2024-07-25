using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public interface IUtilityOrderFactory
    {
        IUtilityOrder Create(Foundations algo, Option option, decimal quantity, decimal? price = null);
    }
}
