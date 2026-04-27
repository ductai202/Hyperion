using Hyperion.DataStructures;

namespace Hyperion.Core;

/// <summary>
/// Orchestrates the data structures for a single worker (or the entire server in single-threaded mode).
/// Go source: storage.go
/// </summary>
public class Storage
{
    public Dict DictStore { get; } = new();
    
    // Set Store
    public System.Collections.Concurrent.ConcurrentDictionary<string, SimpleSet> SetStore { get; } = new();

    // Future phases will add:
    // public Dictionary<string, ZSet> ZSetStore { get; } = new();
    // public Dictionary<string, CMS> CmsStore { get; } = new();
    // public Dictionary<string, Bloom> BloomStore { get; } = new();
}
