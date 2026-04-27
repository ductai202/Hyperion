using Hyperion.Config;

namespace Hyperion.Core;

/// <summary>
/// Proactively deletes expired keys from the database in the background.
/// Uses a random sampling approach similar to Redis.
/// Go source: expire.go
/// </summary>
public class ActiveExpiry
{
    private readonly Storage _storage;

    public ActiveExpiry(Storage storage)
    {
        _storage = storage;
    }

    public void DeleteExpiredKeys()
    {
        while (true)
        {
            int expiredCount = 0;
            int sampleCountRemain = Constants.ActiveExpireSampleSize;
            
            // In a real Redis, this would pick random keys. 
            // Iterating a ConcurrentDictionary in C# gives a point-in-time snapshot, 
            // which acts as a pseudo-random sample for this simple implementation.
            foreach (var kvp in _storage.DictStore.GetExpireDictStore())
            {
                sampleCountRemain--;
                if (sampleCountRemain < 0)
                {
                    break;
                }
                
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > kvp.Value)
                {
                    _storage.DictStore.Del(kvp.Key);
                    expiredCount++;
                }
            }

            // If the number of expired keys in the sample is less than 10%, we can stop.
            if ((double)expiredCount / Constants.ActiveExpireSampleSize <= Constants.ActiveExpireThreshold)
            {
                break;
            }
        }
    }
}
