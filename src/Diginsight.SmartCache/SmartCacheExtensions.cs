﻿using Diginsight.SmartCache.Externalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Diginsight.SmartCache;

public static partial class SmartCacheExtensions
{
    private static readonly MethodInfo UnwrapAsArrayMethod = typeof(SmartCacheExtensions)
        .GetMethod(nameof(UnwrapAsArray), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static SmartCacheBuilder AddSmartCache(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ILoggerFactory? loggerFactory = null,
        Action<SmartCacheCoreOptions>? configureOptions = null
    )
    {
        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }

        return new SmartCacheBuilder(services, configuration, hostEnvironment, loggerFactory)
            .SetSizeLimit(10_000_000)
            .SetLocalCompanion();
    }

    public static object Wrap<TSource>(this ICacheKeyService cacheKeyService, IEnumerable<TSource>? source)
    {
        return cacheKeyService.WrapCore(source ?? [ ]);
    }

    public static object Wrap<TSource, TOrder>(
        this ICacheKeyService cacheKeyService, IEnumerable<TSource>? source, Func<TSource, TOrder> order, IComparer<TOrder>? comparer = null
    )
    {
        return cacheKeyService.WrapCore((source ?? [ ]).OrderBy(order, comparer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object WrapCore<TSource>(this ICacheKeyService cacheKeyService, IEnumerable<TSource> source)
    {
        return new EquatableArray(source.Select(x => cacheKeyService.ToKey(x) ?? x).ToArray());
    }

    public static T UnwrapAs<T>(this object key)
    {
        Type type = typeof(T);
        if (!type.IsArray ||
            type.GetElementType()! is var elementType && elementType == typeof(object))
        {
            return key.UnwrapAsPlain<T>();
        }

        return (T)UnwrapAsArrayMethod
            .MakeGenericMethod(elementType)
            .Invoke(null, [ key ])!;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T UnwrapAsPlain<T>(this object key)
    {
        return key is T unwrapped ? unwrapped : (T)((IUnwrappable)key).Unwrap();
    }

    private static T[] UnwrapAsArray<T>(this object key)
    {
        return Array.ConvertAll(key.UnwrapAsPlain<object[]>(), static x => (T)x);
    }

    [CacheInterchangeName("EA")]
    private sealed class EquatableArray
        : IEquatable<EquatableArray>, IUnwrappable
    {
        [JsonProperty(ItemConverterType = typeof(DetailedJsonConverter))]
        private readonly object?[] array;

        public EquatableArray(object?[] array)
        {
            this.array = array;
        }

        public bool Equals(EquatableArray? other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return ((IStructuralEquatable)array).Equals(other.array, EqualityComparer<object>.Default);
        }

        public override bool Equals(object? obj) => Equals(obj as EquatableArray);

        public override int GetHashCode()
        {
            return ((IStructuralEquatable)array).GetHashCode(EqualityComparer<object>.Default);
        }

        public override string ToString()
        {
            return "[" + string.Join(",", array) + "]";
        }

        public object Unwrap()
        {
            return Array.ConvertAll(array, static x => x is IUnwrappable u ? u.Unwrap() : x);
        }
    }

    public static void PreventSmartCacheDownstreamHeaders(this HttpRequestMessage requestMessage)
    {
#if NET
        requestMessage.Options.Set(SmartCacheHttpMessageHandlerBuilderFilter.PreventSmartCacheDownstreamOptionsKey, true);
#else
        requestMessage.Properties[SmartCacheHttpMessageHandlerBuilderFilter.PreventSmartCacheDownstreamOptionsKey] = true;
#endif
    }
}
