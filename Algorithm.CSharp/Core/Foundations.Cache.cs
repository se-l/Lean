using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations: QCAlgorithm
    {
        public delegate void VoidFunction();
        public VoidFunction Cache<TCacheKey>(VoidFunction decorated, Func<TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        {
            // maxKeys or ttl needs would require a meta dict for the keys. {key: key last added}. For max Keys the n most recent keys stay. rest dropped. To avoid 
            // memory explosion....
            var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
            var cache = new HashSet<TCacheKey>();
            return () =>
            {
                var key = genCacheKey();
                if (!cache.Contains(key)) {
                    cache.Add(key);
                    decorated();
                    if (ttl > 0)
                    {
                        foreach (var cacheKey in cacheMeta.Where(kvp => (Time - kvp.Value).Seconds >= ttl).Select(kvp => kvp.Key))
                        {
                            cache.Remove(cacheKey);
                        }
                    }
                    if (maxKeys > 0 && cache.Count > maxKeys)
                    {
                        foreach (var cacheKey in cacheMeta.OrderByDescending(kvp => kvp.Value).Skip(maxKeys).Select(kvp => kvp.Key))
                        {
                            cache.Remove(cacheKey);
                        }
                    }
                }
            };
        }
        public Func<TResult> Cache<TCacheKey, TResult>(Func<TResult> decorated, Func<TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        {
            var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return () =>
            {
                var key = genCacheKey();
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated();
                cache[key] = result;
                return result;
            };
        }
        public Func<TArgs, TResult> Cache<TCacheKey, TArgs, TResult>(Func<TArgs, TResult> decorated, Func<TArgs, TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        {
            var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return args =>
            {
                var key = genCacheKey(args);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(args);
                cache[key] = result;
                return result;
            };
        }

        public Func<TArg1, TArg2, TResult> Cache<TCacheKey, TArg1, TArg2, TResult>(Func<TArg1, TArg2, TResult> decorated, Func<TArg1, TArg2, TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        {
            var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2) =>
            {
                var key = genCacheKey(arg1, arg2);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2);
                cache[key] = result;
                return result;
            };
        }

        public Func<TArg1, TArg2, TArg3, TResult> Cache<TCacheKey, TArg1, TArg2, TArg3, TResult>(Func<TArg1, TArg2, TArg3, TResult> decorated, Func<TArg1, TArg2, TArg3, TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        {
            var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2, arg3) =>
            {
                var key = genCacheKey(arg1, arg2, arg3);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2, arg3);
                cache[key] = result;
                return result;
            };
        }

        public Func<TArg1, TArg2, TArg3, TArg4, TResult> Cache<TCacheKey, TArg1, TArg2, TArg3, TArg4, TResult>(Func<TArg1, TArg2, TArg3, TArg4, TResult> decorated, Func<TArg1, TArg2, TArg3, TArg4, TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        {
            var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2, arg3, arg4) =>
            {
                var key = genCacheKey(arg1, arg2, arg3, arg4);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2, arg3, arg4);
                cache[key] = result;
                return result;
            };
        }

        public Func<TArg1, TArg2, TArg3, TArg4, TArg5, TResult> Cache<TCacheKey, TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(Func<TArg1, TArg2, TArg3, TArg4, TArg5, TResult> decorated, Func<TArg1, TArg2, TArg3, TArg4, TArg5, TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        {
            var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2, arg3, arg4, arg5) =>
            {
                var key = genCacheKey(arg1, arg2, arg3, arg4, arg5);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2, arg3, arg4, arg5);
                cache[key] = result;
                return result;
            };
        }

        //public Func<TArgs, TResult> Cache<TArgs, TCacheKey, TResult>(Func<TArgs, TResult> decorated, Func<TArgs, TCacheKey> genCacheKey, int maxKeys = 0, int ttl = 0)
        //{
        //    var cacheMeta = new ConcurrentDictionary<TCacheKey, DateTime>();
        //    var cache = new ConcurrentDictionary<TCacheKey, TResult>();
        //    return (args) =>
        //    {
        //        var key = genCacheKey(args);
        //        if (cache.TryGetValue(key, out var result))
        //        {
        //            return result;
        //        }
        //        result = (TResult)decorated.DynamicInvoke(args);
        //        cache[key] = result;
        //        return result;
        //    };
        //}
    }
}
