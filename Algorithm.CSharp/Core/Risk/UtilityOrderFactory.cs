using QuantConnect.Securities.Option;
using System;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class UtilityOrderFactory : IUtilityOrderFactory
    {
        private readonly Type _type;

        public UtilityOrderFactory(Type type)
        {
            _type = type;
        }
        public IUtilityOrder Create(Foundations algo, Option option, decimal quantity, decimal? price = null)
        {
            return (IUtilityOrder)Activator.CreateInstance(_type, algo, option, quantity, price);
        }
    }
}
