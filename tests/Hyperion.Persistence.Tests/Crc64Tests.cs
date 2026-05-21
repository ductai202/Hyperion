using System;
using System.Text;
using Xunit;
using Hyperion.Persistence;

namespace Hyperion.Persistence.Tests;

public class Crc64Tests
{
    [Fact]
    public void Compute_ReturnsCorrectHash()
    {
        // 123456789 test vector for CRC-64-ECMA-182 (Jones)
        byte[] data = Encoding.ASCII.GetBytes("123456789");
        ulong hash = Crc64.Compute(data);
        
        // This is a known test vector for CRC-64-Jones: 14925647086462338541UL
        ulong expected = 14925647086462338541UL;
        Assert.Equal(expected, hash);
    }
}
