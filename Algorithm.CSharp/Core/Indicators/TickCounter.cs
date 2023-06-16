using System;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class TickCounter
    {
        public int Count { get => count;}
        public int Total { get => total;}
        public DateTime StartTime { get; }
        public DateTime EndTime { get; set; }

        private int total;
        private int count;
        private readonly Foundations algo;

        public TickCounter(Foundations algo)
        {
            this.algo = algo;
            StartTime = algo.Time;
        }

        public void Add(int i=1)
        {
            count += i;
            EndTime = algo.Time;
        }

        public int Snap()
        {
            int _count = count;
            total += count;
            count = 0;
            return _count;
        }
    }
}
