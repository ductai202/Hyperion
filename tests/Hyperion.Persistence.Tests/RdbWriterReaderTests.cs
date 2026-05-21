using System;
using System.IO;
using System.Linq;
using Xunit;
using Hyperion.Core;
using Hyperion.DataStructures;
using Hyperion.Persistence;

namespace Hyperion.Persistence.Tests;

public class RdbWriterReaderTests
{
    [Fact]
    public void RoundTrip_EmptyStorage_Succeeds()
    {
        var src = new Storage();
        using var ms = new MemoryStream();
        using (var writer = new RdbWriter(ms))
        {
            writer.WriteHeader();
            writer.WriteMetadata("single");
            writer.WriteDatabase(0, src);
            writer.WriteFooter();
        }

        ms.Position = 0;
        using var fs = new FileStream("test_empty.rdb", FileMode.Create);
        ms.CopyTo(fs);
        fs.Close();

        var reader = new RdbReader("test_empty.rdb");
        var dst = reader.LoadSingle();
        
        Assert.NotNull(dst);
        Assert.Empty(dst.DictStore.GetAllEntries());
        File.Delete("test_empty.rdb");
    }

    [Fact]
    public void RoundTrip_DataStructures_RestoresCorrectly()
    {
        var src = new Storage();
        
        // 1. Strings with TTL
        src.DictStore.Set("key1", src.DictStore.NewObj("key1", "value1", -1));
        src.DictStore.Set("key2", src.DictStore.NewObj("key2", "value2", 10000));
        
        // 2. Hash
        src.HashStore["hkey"] = new System.Collections.Generic.Dictionary<string, string>();
        src.HashStore["hkey"]["f1"] = "v1";
        
        // 3. Set
        src.SetStore["skey"] = new SimpleSet("skey");
        src.SetStore["skey"].Add("m1");
        
        // 4. ZSet
        src.ZSetStore["zkey"] = new ZSet();
        src.ZSetStore["zkey"].Add(10.5, "zm1");
        
        // 5. List
        src.ListStore["lkey"] = new System.Collections.Generic.LinkedList<string>();
        src.ListStore["lkey"].AddLast("item1");
        
        // 6. Bloom Filter
        src.BloomStore["bfkey"] = new Bloom(100, 0.01);
        src.BloomStore["bfkey"].Add("hello");

        // 7. CMS
        var (w, d) = CMS.CalcCMSDim(0.01, 0.01);
        src.CmsStore["cmskey"] = new CMS(w, d);
        src.CmsStore["cmskey"].IncrBy("foo", 5);

        using var ms = new MemoryStream();
        using (var writer = new RdbWriter(ms))
        {
            writer.WriteHeader();
            writer.WriteMetadata("single");
            writer.WriteDatabase(0, src);
            writer.WriteFooter();
        }

        File.WriteAllBytes("test_data.rdb", ms.ToArray());

        var reader = new RdbReader("test_data.rdb");
        var dst = reader.LoadSingle();
        
        Assert.NotNull(dst);
        
        // Verify String
        var v1 = dst.DictStore.Get("key1");
        Assert.NotNull(v1);
        Assert.Equal("value1", v1.Value);
        Assert.True(dst.DictStore.GetExpireDictStore().TryGetValue("key2", out long expireAt));
        Assert.True(expireAt > 0);

        // Verify Hash
        Assert.True(dst.HashStore.TryGetValue("hkey", out var h));
        Assert.True(h.TryGetValue("f1", out var hv));
        Assert.Equal("v1", hv);

        // Verify Set
        Assert.True(dst.SetStore.TryGetValue("skey", out var s));
        Assert.Equal(1, s.IsMember("m1"));

        // Verify ZSet
        Assert.True(dst.ZSetStore.TryGetValue("zkey", out var zs));
        var (exist, score) = zs.GetScore("zm1");
        Assert.True(exist);
        Assert.Equal(10.5, score);

        // Verify List
        Assert.True(dst.ListStore.TryGetValue("lkey", out var l));
        Assert.Single(l);
        Assert.Equal("item1", l.First!.Value);

        // Verify Bloom Filter
        Assert.True(dst.BloomStore.TryGetValue("bfkey", out var bf));
        Assert.True(bf.Exist("hello"));
        Assert.False(bf.Exist("world"));

        // Verify CMS
        Assert.True(dst.CmsStore.TryGetValue("cmskey", out var cms));
        Assert.Equal(5U, cms.Count("foo"));

        File.Delete("test_data.rdb");
    }

    [Fact]
    public void LoadSharded_PartitionsCorrectly()
    {
        var src = new Storage();
        for (int i = 0; i < 100; i++)
        {
            src.DictStore.Set($"key{i}", src.DictStore.NewObj($"key{i}", $"val{i}", -1));
        }

        using var ms = new MemoryStream();
        using (var writer = new RdbWriter(ms))
        {
            writer.WriteHeader();
            writer.WriteMetadata("multi");
            writer.WriteDatabase(0, src);
            writer.WriteFooter();
        }
        File.WriteAllBytes("test_sharded.rdb", ms.ToArray());

        var reader = new RdbReader("test_sharded.rdb");
        var shards = reader.LoadSharded(4);

        Assert.NotNull(shards);
        Assert.Equal(4, shards.Length);

        int totalKeys = 0;
        foreach (var shard in shards)
        {
            totalKeys += shard.DictStore.GetAllEntries().Count();
        }

        Assert.Equal(100, totalKeys);
        File.Delete("test_sharded.rdb");
    }
}
