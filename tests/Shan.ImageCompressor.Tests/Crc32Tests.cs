using System.Text;
using Shan.ImageCompressor.Png;

namespace Shan.ImageCompressor.Tests;

public class Crc32Tests
{
    private static uint Finish(ReadOnlySpan<byte> data) => Crc32.Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    [Fact]
    public void Matches_The_Standard_Check_Value() =>
        Assert.Equal(0xCBF43926u, Finish(Encoding.ASCII.GetBytes("123456789")));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(1000)]
    public void Matches_A_Bytewise_Reference_At_Every_Alignment(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);

        var expected = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            expected ^= b;
            for (var k = 0; k < 8; k++) expected = (expected & 1) != 0 ? 0xEDB88320u ^ (expected >> 1) : expected >> 1;
        }
        Assert.Equal(expected ^ 0xFFFFFFFFu, Finish(data));
    }
}
