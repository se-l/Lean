from QuantConnect.Algorithm import QCAlgorithm


def once_per_algo_time():
    def deco(fn):
        cache = {'last_algo_time': None, 'res': None}

        def wrapper(*args, **kwargs):
            algo = next(iter([arg for arg in args if isinstance(arg, QCAlgorithm)]), None) or \
                next(iter([arg for arg in kwargs.values() if isinstance(arg, QCAlgorithm)]), None)
            if cache['last_algo_time'] != algo.Time:
                cache['last_algo_time'] = algo.Time
                cache['res'] = fn(*args, **kwargs)
                return cache['res']
            else:
                return cache['res']
        return wrapper
    return deco


def once_a_day():
    def deco(fn):
        cache = {'last_algo_time': None, 'res': None}

        def wrapper(*args, **kwargs):
            algo = next(iter([arg for arg in args if isinstance(arg, QCAlgorithm)]), None) or \
                next(iter([arg for arg in kwargs.values() if isinstance(arg, QCAlgorithm)]), None)
            if cache['last_algo_time'] != algo.Time.date():
                cache['last_algo_time'] = algo.Time.date()
                cache['res'] = fn(*args, **kwargs)
                return cache['res']
            else:
                return cache['res']
        return wrapper
    return deco
