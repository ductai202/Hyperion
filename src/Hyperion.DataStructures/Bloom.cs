using System;
using System.Security.Cryptography;
using System.Text;

namespace Hyperion.DataStructures;

/// <summary>
/// A probabilistic data structure used to test whether an element is a member of a set.
/// Ported from bloom.go
/// </summary>
public class Bloom
{
    // These constants are used to calculate the optimal size and number of hash functions for the Bloom Filter.
    // Based on the formulas from https://en.wikipedia.org/wiki/Bloom_filter
    // Optimal number of bits (m) = -(n * ln(p)) / (ln(2)^2)
    // Optimal number of hash functions (k) = (m / n) * ln(2)
    // where n = number of entries, p = desired false positive probability (error rate).
    private const double Ln2 = 0.693147180559945;       // Natural logarithm of 2: ln(2)
    private const double Ln2Square = 0.480453013918201; // Square of natural logarithm of 2: ln(2)^2


    public int Hashes { get; }
    public ulong Entries { get; }
    public double Error { get; }

    private readonly double _bitPerEntry;
    private readonly byte[] _bf;
    private readonly ulong _bits; // size of bf in bits
    private readonly ulong _bytes; // size of bf in bytes

    public Bloom(ulong entries, double errorRate)
    {
        Entries = entries;
        Error = errorRate;

        _bitPerEntry = CalcBpe(errorRate);
        ulong bits = (ulong)(entries * _bitPerEntry);

        if (bits % 64 != 0)
        {
            _bytes = ((bits / 64) + 1) * 8;
        }
        else
        {
            _bytes = bits / 8;
        }

        _bits = _bytes * 8;
        Hashes = (int)Math.Ceiling(Ln2 * _bitPerEntry);
        _bf = new byte[_bytes];
    }

    private static double CalcBpe(double err)
    {
        double num = Math.Log(err);
        return Math.Abs(-(num / Ln2Square));
    }

    public struct HashValue
    {
        public ulong A;
        public ulong B;
    }

    /// <summary>
    /// Calculate 128-bit hash using MD5 (as a substitute for Murmur3 128 in C#)
    /// </summary>
    public HashValue CalcHash(string entry)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(entry);
        byte[] hashBytes = MD5.HashData(inputBytes);

        ulong a = BitConverter.ToUInt64(hashBytes, 0);
        ulong b = BitConverter.ToUInt64(hashBytes, 8);

        return new HashValue { A = a, B = b };
    }

    public void Add(string entry)
    {
        HashValue initHash = CalcHash(entry);
        for (int i = 0; i < Hashes; i++)
        {
            ulong hash = (initHash.A + initHash.B * (ulong)i) % _bits;
            ulong bytePos = hash >> 3; // div 8
            _bf[bytePos] |= (byte)(1 << (int)(hash % 8));
        }
    }

    public bool Exist(string entry)
    {
        HashValue initHash = CalcHash(entry);
        for (int i = 0; i < Hashes; i++)
        {
            ulong hash = (initHash.A + initHash.B * (ulong)i) % _bits;
            ulong bytePos = hash >> 3; // div 8
            if ((_bf[bytePos] & (1 << (int)(hash % 8))) == 0)
            {
                return false;
            }
        }
        return true;
    }

    public void AddHash(HashValue initHash)
    {
        for (int i = 0; i < Hashes; i++)
        {
            ulong hash = (initHash.A + initHash.B * (ulong)i) % _bits;
            ulong bytePos = hash >> 3;
            _bf[bytePos] |= (byte)(1 << (int)(hash % 8));
        }
    }

    public bool ExistHash(HashValue initHash)
    {
        for (int i = 0; i < Hashes; i++)
        {
            ulong hash = (initHash.A + initHash.B * (ulong)i) % _bits;
            ulong bytePos = hash >> 3;
            if ((_bf[bytePos] & (1 << (int)(hash % 8))) == 0)
            {
                return false;
            }
        }
        return true;
    }
}
