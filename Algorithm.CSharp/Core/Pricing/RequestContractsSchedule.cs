using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.IdentityModel.Tokens;
using QuantConnect.Securities.Equity;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class RequestContractsScheduleCfg
    {
        public int StartDaysOffset;
        public string Start;
        public int EndDaysOffset;
        public string End;
        public int NRemaining;
        public int Increment;
        public int NMaximum;
    }

    public class RequestContractsSchedule
    {
        public DateTime Start;
        public DateTime End;
        public int NRemaining;
        public int Increment;
        public int NMaximum;

        public RequestContractsSchedule(RequestContractsScheduleCfg cfg, Foundations algo, Symbol underlying)
        {
            DateTime nextReleaseDate = algo.NextReleaseDate(underlying);

            Start = cfg.Start.IsNullOrEmpty() ? DateTime.MinValue : nextReleaseDate + TimeSpan.FromDays(cfg.StartDaysOffset) + AlgoConfig.GetTimeSpan(cfg.Start);
            End = cfg.End.IsNullOrEmpty() ? DateTime.MaxValue : nextReleaseDate + TimeSpan.FromDays(cfg.EndDaysOffset) + AlgoConfig.GetTimeSpan(cfg.End);
            NRemaining = cfg.NRemaining;
            Increment = cfg.Increment;
            NMaximum = cfg.NMaximum;
        }
    }

    public class RequestContractsHandler
    {
        private readonly Foundations _algo;
        public readonly Equity Underlying;
        private List<RequestContractsSchedule> _schedules;
        public int CurrentNContractsRequested;


        public RequestContractsHandler(Foundations algo, Equity underlying)
        {
            _algo = algo;
            Underlying = underlying;
            SetSchedules();
            CurrentNContractsRequested = InitCurrentNContractsRequested();
        }

        private void SetSchedules()
        {
            List<RequestContractsScheduleCfg> cfgSchedules = _algo.Cfg.RequestContractsSchedules.TryGetValue(Underlying.Symbol.Value, out cfgSchedules) ? cfgSchedules : _algo.Cfg.RequestContractsSchedules[CfgDefault];
            _schedules = cfgSchedules.Select(s => new RequestContractsSchedule(s, _algo, Underlying.Symbol)).ToList();
        }

        private RequestContractsSchedule GetCurrentSchedule()
        {
            return _schedules.FirstOrDefault(s => s.Start <= _algo.Time && s.End > _algo.Time);
        }

        private int InitCurrentNContractsRequested()
        {
            int n;
            var schedule = GetCurrentSchedule();
            if (schedule != null && schedule.Increment > 0)
            {
                n = schedule.Increment;
            } else
            {
                n = 0;
            }
            _algo.Log($"{_algo.Time} RequestContractsHandler.InitCurrentNContractsRequested(): {Underlying.Symbol.Value} - Initialized to {n} contracts");
            return n;
        }

        public int ContractsRemaining()
        {
            var schedule = GetCurrentSchedule();
            return schedule != null ? CurrentNContractsRequested - TotalAbsPosition : 0;
        }

        public int GetContractsRequested()
        {
            UpdateCurrentNContractsRequested();
            return CurrentNContractsRequested;
        }

        public void UpdateCurrentNContractsRequested()
        {
            SetSchedules();
            var schedule = GetCurrentSchedule();
            if (schedule != null && schedule.Increment > 0)
            {
                CurrentNContractsRequested = ContractsRemaining() > schedule.NRemaining ? CurrentNContractsRequested : TotalAbsPosition + schedule.Increment;
                CurrentNContractsRequested = Math.Min(CurrentNContractsRequested, schedule.NMaximum);
                _algo.Log($"{_algo.Time} RequestContractsHandler.UpdateCurrentNContractsRequested(): {Underlying.Symbol.Value} - Updated to {CurrentNContractsRequested} contracts. Max: {schedule.NMaximum}");
            }
        }

        public Symbol Symbol => Underlying.Symbol;

        public int TotalAbsPosition => (int)_algo.Portfolio.Securities.Values.Where(s => s.Type == SecurityType.Option && Underlying(s.Symbol) == Symbol).Sum(s => Math.Abs(s.Holdings.Quantity));
    }
}
