using OozSharp.Exceptions;
using Xunit;

namespace OozSharp.Test;

public class RawBlockTest
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(0x40000)]
    [InlineData(0x40000 + 17)]
    public void UncompressedBlocksCopyExactlyTheirPayload(int length)
    {
        var expected = Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();
        using var stream = new MemoryStream();
        for (var offset = 0; offset < length; offset += 0x40000)
        {
            stream.Write([0xCC, 0x0A]);
            stream.Write(expected.AsSpan(offset, Math.Min(0x40000, length - offset)));
        }
        Assert.Equal(expected, new Kraken().Decompress(stream.ToArray(), length).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedByteQuantaFillEveryByteAcrossBlockBoundaries(bool checksums)
    {
        byte decoder = checksums ? (byte)0x8A : (byte)0x0A;
        byte[] compressed = [0x8C, decoder, 0x07, 0xFF, 0xFF, 0x89,
            0x8C, decoder, 0x07, 0xFF, 0xFF, 0x12];
        var expected = Enumerable.Repeat((byte)0x89, 0x40000).Concat(Enumerable.Repeat((byte)0x12, 17)).ToArray();
        Assert.Equal(expected, new Kraken().Decompress(compressed, expected.Length).ToArray());
    }

    [Fact]
    public void RawQuantumReturnsTheRecordedBytes()
    {
        byte[] compressed = [0x8C, 0x0A, 0x00, 0x00, 0x02, 0x19, 0x28, 0x37];
        Assert.Equal(new byte[] { 0x19, 0x28, 0x37 }, new Kraken().Decompress(compressed, 3).ToArray());
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x8C })]
    [InlineData(new byte[] { 0x8C, 0x0A })]
    [InlineData(new byte[] { 0x8C, 0x0A, 0x07, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0x8C, 0x8A, 0x00, 0x00, 0x02 })]
    [InlineData(new byte[] { 0x8C, 0x0A, 0x00, 0x00, 0x02, 0x19 })]
    [InlineData(new byte[] { 0xCC, 0x0A, 0x19 })]
    public void IncompleteBlocksThrowRatherThanReadingOutsideTheBuffer(byte[] compressed)
    {
        Assert.Throws<DecoderException>(() => new Kraken().Decompress(compressed, 3));
    }
}
