using System;

namespace QuantConnect.Algorithm.CSharp.Core.IO
{
    public class TargetPortfoliosEventArgs : EventArgs
    {
        public TargetPortfoliosEventArgs(ResponseTargetPortfoliosPb responseTargetPortfolios)
        {
            ResponseTargetPortfolios = responseTargetPortfolios;
        }

        public ResponseTargetPortfoliosPb ResponseTargetPortfolios { get; }
    }

    public class ResultStressTestDsEventArgs : EventArgs
    {
        public ResultStressTestDsEventArgs(ResultStressTestDsPb resultStressTestDs)
        {
            ResultStressTestDs = resultStressTestDs;
        }

        public ResultStressTestDsPb ResultStressTestDs { get; }
    }

    public class CmdFetchTargetPortfolioEventArgs : EventArgs
    {
        public CmdFetchTargetPortfolioEventArgs(CmdFetchTargetPortfolio cmdFetchTargetPortfolio)
        {
            CmdFetchTargetPortfolio = cmdFetchTargetPortfolio;
        }

        public CmdFetchTargetPortfolio CmdFetchTargetPortfolio { get; }
    }

    public class CmdCancelOIDEventArgs : EventArgs
    {
        public CmdCancelOIDEventArgs(CmdCancelOID cmdCancelOid)
        {
            CmdCancelOid = cmdCancelOid;
        }

        public CmdCancelOID CmdCancelOid { get; }
    }

    public class CmdCfgOverrideEventArgs : EventArgs
    {
        public CmdCfgOverrideEventArgs(CmdCfgOverride cmdCfgOverride)
        {
            CmdCfgOverride = cmdCfgOverride;
        }

        public CmdCfgOverride CmdCfgOverride { get; }
    }

    public class ResponseKalmanInitEventArgs : EventArgs
    {
        public ResponseKalmanInitEventArgs(ResponseKalmanInitPb responseKalmanInit)
        {
            ResponseKalmanInit = responseKalmanInit;
        }

        public ResponseKalmanInitPb ResponseKalmanInit { get; }
    }

    public class ResponseSSVICalibrationEventArgs : EventArgs
    {
        public ResponseSSVICalibrationEventArgs(ResponseSSVICalibrationPb responseSSVICalibration)
        {
            ResponseSSVICalibration = responseSSVICalibration;
        }

        public ResponseSSVICalibrationPb ResponseSSVICalibration { get; }
    }

    public class ResponsePfRiskScenariosEventArgs : EventArgs
    {
        public ResponsePfRiskScenariosEventArgs(ResponsePfRiskScenariosPb responsePfRiskScenarios)
        {
            ResponsePfRiskScenarios = responsePfRiskScenarios;
        }

        public ResponsePfRiskScenariosPb ResponsePfRiskScenarios { get; }
    }
}
