from core.stubs import *


class IQFeedTest(QCAlgorithm):

    def Initialize(self):
        self.UniverseSettings.Resolution = self.resolution = Resolution.Minute
        self.SetStartDate(2023, 5, 5)
        self.SetEndDate(2023, 5, 8)
        self.SetCash(100000)
        self.SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin)
        self.UniverseSettings.DataNormalizationMode = DataNormalizationMode.SplitAdjusted

        self.hedge_ticker = ['SPY']
        self.option_ticker = ['HPE', 'IPG']
        self.option_ticker += ['AKAM', 'AOS', 'A', 'MO', 'FL', 'ALL', 'ARE', 'ZBRA', 'AES', 'APD', 'ALLE', 'LNT', 'ZTS', 'ZBH']
        self.ticker = self.option_ticker + self.hedge_ticker
        self.equities: List[Equity] = []
        self.options: List[Symbol] = []  # Canonical symbols
        self.option_chains: Dict[Symbol, Union[OptionChain, None]] = {}

        subscriptions = 0
        for ticker in self.ticker:
            equity = self.AddEquity(ticker, resolution=self.resolution)
            subscriptions += 1
            self.equities.append(equity)
            if ticker in self.option_ticker:
                # QC only support Minute and larger resolution for options.
                # https://www.quantconnect.com/docs/v2/writing-algorithms/securities/asset-classes/equity-options/requesting-data#01-Introduction
                option  = self.AddOption(ticker, resolution=self.resolution)
                self.options.append(option.Symbol)

        self.AddUniverseSelection(ManualUniverseSelectionModel([e.Symbol for e in self.equities]))

    def OnData(self, data: Slice):
        # Check if any of the data requests failed
        self.Log(data)
        if self.IsWarmingUp:
            return

        for symbol in data.keys():  # Could be Equity, Option, Dividend
            if str(symbol) in self.hedge_ticker:
                continue

            equity, option = self.equity_option_from_symbol(symbol)
            if option:
                if chain := data.OptionChains.get(option):
                    self.option_chains[equity] = self.option_chains[option] = chain
                    for contract in chain:
                        self.Log(f'{contract.Symbol} {contract.LastPrice} {contract.BidPrice} {contract.BidSize} {contract.AskPrice} {contract.AskSize}')

    def equity_option_from_symbol(self, symbol: Symbol) -> Tuple[Union[Equity, None], Union[Option, None]]:
        """Not handling contracts..."""
        security: Union[Equity, OptionContract, Option] = self.Securities.get(symbol)

        if symbol.SecurityType == SecurityType.Option:
            if getattr(security, 'IsOptionContract', False):
                contract: OptionContract = security
                equity: Union[Equity, Security] = self.Securities[getattr(contract, 'UnderlyingSymbol', contract.Underlying.Symbol)]
                option = next((o for o in self.options if o.Underlying == equity.Symbol), None)
            else:  # Option
                option: Option = security
                equity = self.Securities.get(option.Underlying.Symbol)
        elif symbol.SecurityType == SecurityType.Equity:
            equity = security
            option = next((o for o in self.options if o.Underlying == equity.Symbol), None)

            if option is None:
                self.Log(f'Failed to derive Option from Equity {equity.Symbol}. Not processing.... Missed a subscription?')
        else:
            self.Log(f'Failed to derive Security Type of {symbol}. Not processing...')
            equity = option = None
        return equity, option
