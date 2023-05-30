from core.stubs import *
from core.utils import is_liquid


def filter_option_no_position(option_contracts: List[OptionContract], algo) -> List[OptionContract]:
    return [c for c in option_contracts if algo.Portfolio[c.Symbol].Quantity == 0]


def filter_option_open_interest(option_contracts: List[OptionContract], open_interest_range: Tuple[Decimal, Decimal] = (50, None)) -> List[OptionContract]:
    contracts = []
    if open_interest_range[0]:
        contracts += [x for x in option_contracts if x.OpenInterest >= open_interest_range[0]]
    if open_interest_range[1]:
        contracts += [x for x in option_contracts if x.OpenInterest <= open_interest_range[1]]
    return contracts


def filter_option_volume(option_contracts: List[OptionContract], volume_range: Tuple[Decimal, Decimal] = (1, None)) -> List[OptionContract]:
    contracts = []
    if volume_range[0]:
        contracts += [x for x in option_contracts if x.Volume >= volume_range[0]]
    if volume_range[1]:
        contracts += [x for x in option_contracts if x.Volume <= volume_range[1]]
    return contracts


def filter_is_liquid(option_contracts: List[OptionContract], algo, window=3) -> List[OptionContract]:
    return [c for c in option_contracts if is_liquid(algo=algo, contract=c, window=window)]


def filter_contracts_by_strike(option_contracts: List[OptionContract], strike_range: Tuple[Decimal, Decimal] = (-3, 3)) -> List[OptionContract]:
    contracts = []
    if strike_range[0]:
        contracts = [x for x in option_contracts if x.Strike >= strike_range[0]]
    if strike_range[1]:
        contracts = [x for x in option_contracts if x.Strike <= strike_range[1]]
    return contracts


def options_filter_not_in_use(
    options_contracts_chain,
    strike_count=3,  # no of strikes around underlying price => for universe selection
    mix_expiry_delta_days=7,  # min num of days to expiration => for uni selection
    max_expiry_delta_days=60,  # max num of days to expiration => for uni selection
):
    """
    The options filter function.
    Filter the options chain we only have relevant strikes & expiration dates.
    Volume / open interest
    """
    return options_contracts_chain.IncludeWeeklys() \
        .Strikes(-strike_count, strike_count) \
        .Expiration(timedelta(mix_expiry_delta_days), timedelta(max_expiry_delta_days))
