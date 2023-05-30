# if data.Bars.ContainsKey(symbol) and data.QuoteBars.ContainsKey(symbol):
            #     trade_bar: TradeBar = data.Bars[symbol]
            #     quote_bar: QuoteBar = data.QuoteBars[symbol]
            #     self.Log(f'{symbol} {trade_bar.Price} {getattr(quote_bar.Bid, "Close")} {getattr(quote_bar.Ask, "Close")}')
            # elif data.Bars.ContainsKey(symbol):
            #     trade_bar: TradeBar = data.Bars[symbol]
            #     self.Log(f'{symbol} {trade_bar.Price}')
            # else:
            #     self.Log(f'{symbol} not in quote bar and trade bar')

