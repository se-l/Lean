from QuantConnect.Orders import OrderDirection

OPEN = 'open'
HIGH = 'high'
LOW = 'low'
CLOSE = 'close'
VOLUME = 'volume'

OHLC = [OPEN, HIGH, LOW, CLOSE]
OHLCV = [OPEN, HIGH, LOW, CLOSE, VOLUME]
BP = 1 / 10_000
DIRECTION2NUM = {OrderDirection.Buy: 1, OrderDirection.Sell: -1}
NUM2DIRECTION = {v: k for k, v in DIRECTION2NUM.items()}

# OPTION PRICING
STEPS = 200
RISK_FREE_RATE = 0.0466

# USER
SEB = 'seb'
