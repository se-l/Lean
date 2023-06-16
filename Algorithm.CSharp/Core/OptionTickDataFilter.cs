using QuantConnect.Data.Market;
using QuantConnect.Data;
using QuantConnect.Securities;
using QuantConnect.Securities.Interfaces;
using System.Collections.Generic;
using System;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class OptionTickDataFilter : ISecurityDataFilter
    {
        private readonly Foundations algo;
        private readonly Dictionary<Security, Tick> previousValidTick = new();
        private DateTime lastPass;

        /// <summary>
        /// Save instance of the algorithm namespace
        /// </summary>
        /// <param name="algo"></param>
        public OptionTickDataFilter(Foundations algo)
        {
            this.algo = algo;
            lastPass = algo.Time;
        }

        /// <summary>
        /// Filter out a tick from this vehicle, with this new data:
        /// </summary>
        /// <param name="data">New data packet:</param>
        /// <param name="asset">Vehicle of this filter.</param>
        public bool Filter(Security asset, BaseData data)
        {
            // TRUE -->  Accept Tick
            // FALSE --> Reject Tick
            var tick = data as Tick;

            // This is a tick bar
            if (tick != null)
            {
                algo.TickCounterFilter.Add();

                // Test brokerage.Scan() whole algo is run AFTER filtering for suspicious ticks
                //if (tick.EndTime - lastPass < TimeSpan.FromMilliseconds(2000))
                //{
                //    return false;
                //}


                if (tick.BidPrice == 0 || tick.AskPrice == 0)
                {
                    return false;
                }
                if (previousValidTick.TryGetValue(asset, out Tick prevTick))
                {
                    if (tick.BidPrice == prevTick.BidPrice && tick.AskPrice == prevTick.AskPrice)
                    {
                        return false;
                    }

                    if (tick.EndTime - prevTick.EndTime < TimeSpan.FromMilliseconds(5000))
                    {
                        return false;
                    }
                }

                // Emit Ticks Event based. Requirements:
                // Need new ticks whenever underlying mid prices changes. Adjustment of bid ask prices may take multiple ticks
                // Annoying having to interact with another feed to determine the behavior of this feed here... Check for a flag on algo...
                // Need to sample IV over time, hence need regularly ticks. For sampling purposes, a consolidator could be good. Overengineered? Sampling be definition is not ALL ticks.
                //if (algo.EmitTick[asset])
                //{
                //    return true;
                //}
                

                previousValidTick[asset] = tick;
            }

            return true;
        }
    }
}
