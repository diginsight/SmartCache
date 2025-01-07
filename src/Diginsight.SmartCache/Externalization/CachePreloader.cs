﻿using Diginsight.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Diginsight.SmartCache.Externalization;

public sealed class CachePreloader : ICachePreloader
{
#if NET
    private static Random SharedRandom
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Random.Shared;
    }
#else
    private static readonly Random SharedRandom = new ();
#endif

    private readonly ILogger logger;
    private readonly ICacheCompanion companion;
    private readonly TimeProvider timeProvider;

    public CachePreloader(
        ILogger<CachePreloader> logger,
        ICacheCompanion companion,
        TimeProvider? timeProvider = null
    )
    {
        this.logger = logger;
        this.companion = companion;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task PreloadAsync<T>(object key, Func<Task<T>> fetchAsync)
    {
        using Activity? activity = SmartCacheObservability.ActivitySource.StartMethodActivity(logger, () => new { key });

        CachePayloadHolder<object> keyHolder = new CacheKeyHolder(key);

        SmartCacheObservability.Instruments.Preloads.Add(1);

        DateTimeOffset timestamp = SmartCache.Truncate(timeProvider.GetUtcNow());

        T value;
        StrongBox<double> latencyMsecBox = new ();
        using (SmartCacheObservability.Instruments.FetchDuration.StartLap(latencyMsecBox, SmartCacheObservability.Tags.Type.Preload))
        {
            value = await fetchAsync();
        }

        logger.LogDebug("Fetched in {LatencyMsec} ms", (long)latencyMsecBox.Value);

        TaskUtils.RunAndForget(() => NotifyAsync(keyHolder, timestamp, value));
    }

    private async Task NotifyAsync<TValue>(CachePayloadHolder<object> keyHolder, DateTimeOffset creationDate, TValue value)
    {
        using Activity? activity = SmartCacheObservability.ActivitySource.StartMethodActivity(logger, () => new { key = keyHolder.Payload, creationDate });

        IEnumerable<CacheEventNotifier> eventNotifiers = await companion.GetAllEventNotifiersAsync();
        if (!eventNotifiers.Any())
        {
            return;
        }

        string selfLocationId = companion.SelfLocationId;
        CacheMissDescriptor descriptor = new (selfLocationId, keyHolder.Payload, creationDate, selfLocationId, (typeof(TValue), value));
        CachePayloadHolder<CacheMissDescriptor> descriptorHolder = new (descriptor, SmartCacheObservability.Tags.Subject.Value);

        CacheEventNotifier[] eventNotifiersArray = eventNotifiers.ToArray();
        eventNotifiersArray[SharedRandom.Next(eventNotifiersArray.Length)].NotifyCacheMissAndForget(descriptorHolder);
    }
}
