using System.Collections.Generic;

namespace Hyperion.DataStructures;

/// <summary>
/// A Sorted Set implementation using a combination of a Dictionary and a Skiplist.
/// The dictionary provides O(1) access to scores, while the skiplist provides O(log(N)) ranking and ranges.
/// </summary>
public class ZSet
{
    private readonly Skiplist _zskiplist;
    private readonly Dictionary<string, double> _dict;

    public ZSet()
    {
        _zskiplist = new Skiplist();
        _dict = new Dictionary<string, double>();
    }

    public int Add(double score, string ele)
    {
        if (string.IsNullOrEmpty(ele))
            return 0;

        if (_dict.TryGetValue(ele, out double curScore))
        {
            if (curScore != score)
            {
                var znode = _zskiplist.UpdateScore(curScore, ele, score);
                if (znode != null)
                {
                    _dict[ele] = znode.Score;
                }
            }
            return 0; // Updated, not added
        }

        var newNode = _zskiplist.Insert(score, ele);
        _dict[ele] = newNode.Score;
        return 1; // Added new
    }

    public int Rem(string ele)
    {
        if (_dict.TryGetValue(ele, out double score))
        {
            _zskiplist.Delete(score, ele);
            _dict.Remove(ele);
            return 1;
        }
        return 0;
    }

    public (long rank, double score) GetRank(string ele, bool reverse)
    {
        if (!_dict.TryGetValue(ele, out double score))
        {
            return (-1, 0);
        }

        long rank = _zskiplist.GetRank(score, ele);
        if (reverse)
        {
            rank = _zskiplist.Length - rank;
        }
        else
        {
            rank--; // 0-based
        }
        return (rank, score);
    }

    public (bool exist, double score) GetScore(string ele)
    {
        bool exist = _dict.TryGetValue(ele, out double score);
        return (exist, score);
    }

    public int Len()
    {
        return _dict.Count;
    }

    /// <summary>
    /// Returns elements within the specified rank range (0-based inclusive).
    /// </summary>
    public List<string> GetRange(long start, long stop, bool reverse = false)
    {
        long length = _zskiplist.Length;
        if (start < 0) start += length;
        if (stop < 0) stop += length;

        if (start < 0) start = 0;
        if (stop < 0) stop = 0;
        if (start >= length || start > stop) return new List<string>();
        if (stop >= length) stop = length - 1;

        var result = new List<string>();
        long rank = 0;
        var node = _zskiplist.Head.Levels[0].Forward;

        // Simple traversal for range, can be optimized by using span in Skiplist,
        // but linear traversal is fine for basic implementation.
        while (node != null && rank <= stop)
        {
            if (rank >= start)
            {
                result.Add(node.Ele);
            }
            node = node.Levels[0].Forward;
            rank++;
        }

        if (reverse)
        {
            result.Reverse();
        }

        return result;
    }
}
