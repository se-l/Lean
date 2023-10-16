using System;
using QuantConnect.Securities.Option;
using QuantConnect.Indicators;
using System.IO;
using QuantConnect.Securities.Equity;
using System.Collections.Generic;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class PutCallRatioIndicator : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider
    {
        public Symbol Symbol { get; }
        public Symbol Underlying { get => Option == null ? Option.Underlying.Symbol : Symbol; }
        public Option? Option { get; internal set; }
        public Equity Equity { get; internal set; }
        public int VolumePut { get; internal set; }
        public int VolumeCall { get; internal set; }
        public decimal Ratio() => VolumeCall > 0 ? VolumePut / VolumeCall : 0;

        private readonly bool _isEquity;
        private readonly TimeSpan _window;
        private List<IndicatorDataPoint> _calls = new();
        private List<IndicatorDataPoint> _puts = new();

        private readonly Foundations _algo;
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;

        public int WarmUpPeriod => 3;//days
        private bool _isReady; 
        public override bool IsReady => _isReady;

        /// <summary>
        /// Automatic Indicator indicator receiving trade bars of a given option contract. This in turns updates a put call ratio indicator of its underlying.
        /// </summary>
        /// <param name="option"></param>
        /// <param name="algo"></param>
        public PutCallRatioIndicator(Option option, Foundations algo, TimeSpan window) : base($"PutCallRatio Option {option.Symbol}")
        {
            _algo = algo;
            _isEquity = false;
            _window = window;
            Symbol = option.Symbol;
            Equity = _algo.Securities[option.Underlying.Symbol] as Equity;
        }

        public PutCallRatioIndicator(Equity equity, Foundations algo, TimeSpan window) : base($"PutCallRatio Underlying {equity.Symbol}")
        {
            _algo = algo;
            _isEquity = true;
            _window = window;
            Symbol = equity.Symbol;
            Equity = equity;

            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", Symbol.Value, "PutCallRatios.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new StreamWriter(_path, true)
            {
                AutoFlush = true
            };
            _writer.WriteLine("Time,Symbol,VolumeCall,VolumePut,Ratio");
        }

        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if (input.Symbol.SecurityType != SecurityType.Option) throw new ArgumentException($"PutCallRatio.ComputeNextValue: Unsupported SecurityType: {input.Symbol.SecurityType}.");
            
            switch (_isEquity)
            {
                case false:
                    _algo.PutCallRatios[Equity.Symbol].ComputeNextValue(input);
                    break;
                case true:
                    switch (input.Symbol.ID.OptionRight)
                    {
                        case OptionRight.Call:
                            VolumeCall += (int)input.Value;
                            _calls.Add(input);
                            break;
                        case OptionRight.Put:
                            VolumePut += (int)input.Value;
                            _puts.Add(input);
                            break;
                        default:
                            throw new ArgumentException($"PutCallRatio.ComputeNextValue: Unsupported OptionRight: {input.Symbol.ID.OptionRight}.");
                    }
                    SubtractOutOfWindowInputs(_calls);
                    SubtractOutOfWindowInputs(_puts);                    
                    break;
            }
            return Ratio();
        }

        public void Update(TradeBar bar)
        {
            ComputeNextValue(new IndicatorDataPoint(bar.Symbol, bar.EndTime, bar.Volume));
        }

        private void SubtractOutOfWindowInputs(List<IndicatorDataPoint> inputs)
        {
            IndicatorDataPoint item;
            int ixRemove = -1;
            for (int i = 0; i < inputs.Count; i++)
            {
                item = inputs[i];
                if (!(item.Time >= _algo.Time - _window))
                {
                    ixRemove = i;
                    VolumeCall -= (int)item.Value;
                }
                break;
            }
            if (ixRemove > -1)
            {
                inputs.RemoveRange(0, ixRemove+1);
                _isReady = true;
            }
        }

        /// <summary>
        /// Write PutCallRatios to CSV
        /// </summary>
        public void Export()
        {
            if (_writer == null) return;
            _writer.WriteLine($"{_algo.Time},{Symbol},{VolumeCall},{VolumePut},{Ratio()}");
        }

        public void Dispose()
        {
            if (_writer == null)
            {
                _algo.Log($"{this.GetType().BaseType.Name}.Write(): _writer is null.");
                return;
            }
            else if (_writer.BaseStream == null)
            {
                _algo.Log($"{this.GetType().BaseType.Name}.Write(): _writer is closed.");
                return;
            }

            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}
