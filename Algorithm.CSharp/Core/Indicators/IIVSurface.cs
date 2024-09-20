using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public interface IIVSurface : IDisposable
    {
        public double IV(double k, DateTime tenor, OptionRight right, DateTime calcDate);
        public double IV(Option option);
        public bool IsCalibrated { get; }
        public bool HasParams(Option option);
        public double AtmIv();
        public double? IVdS(Symbol symbol);
        public double SkewStrike();
        public void WriteCsvRows();
        public void SetModelParams(SSVIParamsDictionary modelParams);
        public void SetModelParams(object sender, KalmanOnUpdateEventArgs<SSVIParamsDictionary> e);
    }
}
