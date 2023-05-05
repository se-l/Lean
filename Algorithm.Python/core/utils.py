import logging
import math
import System

from itertools import combinations, chain
from dataclasses import dataclass, astuple
from scipy.stats import pearsonr

from core.stubs import *
from core.constants import CLOSE

logger = logging.Logger(__name__)


def handle_error(default_return=None):
    def outer(func):
        def inner(*args, **kwargs):
            try:
                return func(*args, **kwargs)
            except Exception as e:
                logger.warning(f'{__name__}: {e}')
                return default_return

        return inner

    return outer


@dataclass
class MMWindow:
    start: time = None
    stop: time = None


@dataclass
class DCBase:
    def __array__(self):
        return np.array(astuple(self))
    #
    # def __matmul__(self, b):
    #     return np.array(astuple(self)) @ (np.array(b) if isinstance(b, DCBase) else b)


@dataclass
class RiskLmt(DCBase):
    position: float = None
    unhedged: float = None


def name(enum: Union[System.Enum, Any], val: Union[int, Any]) -> str:
    return dict(zip(enum.GetValues(enum), enum.GetNames(enum))).get(val, '')


def round_tick(x, tick_size, ceil=None, reference=None) -> float:
    if reference and abs((reference - x)) < (tick_size/2):
        return reference
    if ceil is True:
        return math.ceil(x*(1/tick_size))/(1/tick_size)
    elif ceil is False:
        return math.floor(x*(1/tick_size))/(1/tick_size)
    else:
        return round(x*(1/tick_size))/(1/tick_size)


def rolling_pearson_corr(x: np.ndarray | List, y: np.ndarray | List, window: Tuple[int]) -> Tuple[np.ndarray, np.ndarray]:
    """
    Calculate the rolling Pearson correlation coefficient between two time series with a parameterized lookback window.

    Parameters:
    x (array-like): First time series.
    y (array-like): Second time series.
    window (int): Lookback window size.

    Returns:
    corr (ndarray): Array of Pearson correlation coefficients.
    """
    corr = np.empty(len(x))
    p_val = np.empty(len(x))
    corr[:] = np.nan
    p_val[:] = np.nan
    for i in range(window - 1, len(x)):
        corr[i], p_val[i] = pearsonr(x[i - window + 1:i + 1], y[i - window + 1:i + 1])
    return corr, p_val


def get_correlation_tensor(algo: QCAlgorithm, symbols: List[Symbol], windows: List[int] = (10, 30, 180)) -> np.ndarray:
    metrics = list(chain(*[[f'corr_{w}d', f'corr_{w}d_p'] for w in windows]))
    n_metrics = len(metrics)
    history = {sym: algo.History(sym, 20, Resolution.Daily)[CLOSE] for sym in symbols}

    log_returns = {}
    for sym, prices in history.items():
        s0 = np.log(prices.values)
        log_returns[sym] = s0[1:] - s0[:-1]
    days = len(log_returns[list(log_returns.keys())[0]])  # daily returns
    shape = (len(symbols), len(symbols), n_metrics, days)
    arr = np.full(shape, np.nan)
    algo.Debug(f'Creating Pearson corr array of shape: {arr.shape}')

    for sym1, sym2 in combinations(log_returns.keys(), 2):
        for window in windows:
            metric = f'corr_{window}d'
            metric_p = f'corr_{window}d_p'
            print(f'Getting Pearson coefficient for {sym1}|{sym2} window-{window}')
            v1, v2 = rolling_pearson_corr(log_returns[sym1], log_returns[sym2], window+1)
            arr[symbols.index(sym1)][symbols.index(sym2)][metrics.index(metric)] = v1
            arr[symbols.index(sym1)][symbols.index(sym2)][metrics.index(metric_p)] = v2
    return arr


def history_symbol_bar(algo: QCAlgorithm, symbol: Symbol, window: int = 30, start=None, end=None, resolution=Resolution.Daily, slice=TradeBar, cache={}) -> List[QuoteBar | TradeBar | Tick]:
    if symbol == cache.get('symbol') and resolution == cache.get('resolution') and start == cache.get('start') and end == cache.get('end') and cache.get(slice) == slice:
        return cache['history']
    else:
        history = algo.History[slice](symbol=symbol, start=start or algo.Time.date() - timedelta(days=window), end=end or algo.Time.date(), resolution=resolution)
        cache['symbol'] = symbol
        cache['resolution'] = resolution
        cache['start'] = start
        cache['end'] = end
        cache['slice'] = slice
        cache['history'] = history
        return history


def is_liquid(algo: QCAlgorithm, contract: OptionContract, window: int = 3, start=None, end=None, resolution=Resolution.Daily) -> bool:
    trade_bars: List[TradeBar] = history_symbol_bar(algo, contract.Symbol, window=window, start=start, end=end, resolution=resolution, slice=TradeBar)
    return sum([bar.Volume for bar in trade_bars]) > 0


def prev_business_day(dt: date):
    offset = max(1, (dt.weekday() + 6) % 7 - 3)
    return dt - timedelta(days=offset)


def get_avg_spread(algo: QCAlgorithm, contract: OptionContract, start: datetime.date = None) -> float:
    start = start or prev_business_day(algo.Time.date())
    quote_bars: List[QuoteBar] = history_symbol_bar(algo, contract.Symbol, start=start, end=start, resolution=Resolution.Minute, slice=QuoteBar)
    bids = [b.Bid for b in quote_bars]
    asks = [b.Ask for b in quote_bars]
    return (np.mean(asks) - np.mean(bids)) if (bids and asks) else 0


def get_contract(algo: QCAlgorithm, symbol: Symbol) -> Union[OptionContract, None]:
    """Gives Greeks. Must pass Symbol of a contract"""
    contract = algo.option_chains[algo.Securities.get(symbol).Underlying].Contracts.get(symbol)
    if not contract:
        algo.Log(f"Did not find contract for {symbol}. Not fully initialized?")
    return contract


def hist_vol(prices: pd.Series, span=30, annualized=True, ddof=1):
    std = np.log(prices / prices.shift(1))[-span:].std(ddof=ddof)
    return std * np.sqrt(252) if annualized else std


def mid_price(algo: QCAlgorithm, symbol: Symbol | Security | OptionContract) -> float:
    sec = algo.Securities[symbol] if isinstance(symbol, Symbol) else symbol
    return sec.AskPrice + sec.BidPrice / 2


def tick_size(algo: QCAlgorithm, symbol: Symbol | Security | OptionContract, cache={}) -> float:
    if symbol in cache:
        return cache[symbol]
    else:
        if hasattr(symbol, 'Symbol'):  # Symbol
            sec = algo.Securities[symbol.Symbol]  # OptionContract, Security            
        else:            
            sec = algo.Securities[symbol]  # Symbol

        cache[symbol] = sec.SymbolProperties.MinimumPriceVariation
        return cache[symbol]
