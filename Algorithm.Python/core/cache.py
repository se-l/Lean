from functools import lru_cache
from inspect import getfullargspec, ismethod
from collections import deque


@lru_cache(maxsize=None)
def cached_getfullargspec(func):
    return getfullargspec(func)


def getcallargs(func, /, *positional, **named):
    """Get the mapping of arguments to values.

    A dict is returned, with keys the function argument names (including the
    names of the * and ** arguments, if any), and values the respective bound
    values from 'positional' and 'named'."""
    spec = cached_getfullargspec(func)
    args, varargs, varkw, defaults, kwonlyargs, kwonlydefaults, ann = spec
    f_name = func.__name__
    arg2value = {}

    if ismethod(func) and func.__self__ is not None:
        # implicit 'self' (or 'cls' for classmethods) argument
        positional = (func.__self__,) + positional
    num_pos = len(positional)
    num_args = len(args)
    num_defaults = len(defaults) if defaults else 0

    n = min(num_pos, num_args)
    for i in range(n):
        arg2value[args[i]] = positional[i]
    if varargs:
        arg2value[varargs] = tuple(positional[n:])
    possible_kwargs = set(args + kwonlyargs)
    if varkw:
        arg2value[varkw] = {}
    for kw, value in named.items():
        arg2value[kw] = value
    # if num_pos > num_args and not varargs:
    #     _too_many(f_name, args, kwonlyargs, varargs, num_defaults,
    #                num_pos, arg2value)
    if num_pos < num_args:
        req = args[:num_args - num_defaults]
        # for arg in req:
        #     if arg not in arg2value:
        #         _missing_arguments(f_name, req, True, arg2value)
        for i, arg in enumerate(args[num_args - num_defaults:]):
            if arg not in arg2value:
                arg2value[arg] = defaults[i]
    # missing = 0
    # for kwarg in kwonlyargs:
    #     if kwarg not in arg2value:
    #         if kwonlydefaults and kwarg in kwonlydefaults:
    #             arg2value[kwarg] = kwonlydefaults[kwarg]
    #         else:
    #             missing += 1
    # if missing:
    #     _missing_arguments(f_name, kwonlyargs, False, arg2value)
    return arg2value


def cache(f_key=lambda **kw: None, maxsize=None, ttl=None):
    """This decorator will pass the kwargs form of the function signature to the f_key function to generate a key for the cache."""
    def deco(func):
        cch = {}
        # maxsize = max(maxsize, 1) @ if maxsize else None
        key_deque = deque(maxlen=maxsize)

        def wrapper(*args, **kwargs):
            key = f_key(**getcallargs(func, *args, **kwargs))

            if key in cch:
                return cch[key]
            else:
                cch[key] = func(*args, **kwargs)
                key_deque.append(key)

                if maxsize and len(cch) > maxsize:
                    # remove the oldest key from the queue and dictionary
                    if key != key_deque[0]:
                        oldest_key = key_deque.popleft()
                        del cch[oldest_key]

                return cch[key]
        return wrapper
    return deco
