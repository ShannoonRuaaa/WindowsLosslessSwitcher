using System.Text;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AlacBitDepthReaderTests
{
    [Theory]
    [InlineData(44100, 16)]
    [InlineData(192000, 24)]
    public void ReadsRateAndDepthFromSameCookie(int sampleRate, int depth)
    {
        var bytes = BuildAlacFile(depth, 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), (uint)sampleRate);
        using var stream = new MemoryStream(bytes);
        Assert.Equal(new AlacStreamInfo(sampleRate, depth), AlacBitDepthReader.TryReadFormat(stream));
    }

    [Fact]
    public void MetadataProbe_ReadsAlacWithoutRequiringTagLibContainerMetadata()
    {
        var bytes = BuildAlacFile(24, 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), 96000);
        var path = Path.Combine(Path.GetTempPath(), $"wls-alac-{Guid.NewGuid():N}.m4a");
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.Equal(new AudioFileProbeResult(96000, 24, "ALAC"), new TagLibAudioFileMetadataProbe().Probe(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void ReadsTrueBitDepthFromAlacMagicCookie(int bitDepth)
    {
        // The MP4 sample entry advertises the legacy samplesize of 16; the real depth lives in the
        // ALAC cookie and must win.
        using var stream = new MemoryStream(BuildAlacFile(bitDepth, legacySampleSize: 16));

        Assert.Equal(bitDepth, AlacBitDepthReader.TryReadBitDepth(stream));
    }

    [Fact]
    public void ReturnsNullForAacContainer()
    {
        // An 'mp4a' (AAC) sample entry carries no ALAC cookie, so there is no depth to read.
        using var stream = new MemoryStream(BuildAacFile());

        Assert.Null(AlacBitDepthReader.TryReadBitDepth(stream));
    }

    [Fact]
    public void ReturnsNullForTruncatedFile()
    {
        using var stream = new MemoryStream([0x00, 0x00, 0x00]);

        Assert.Null(AlacBitDepthReader.TryReadBitDepth(stream));
    }

    private static byte[] BuildAlacFile(int bitDepth, int legacySampleSize)
    {
        // Inner 'alac' config box: 4 bytes version/flags + 24-byte magic cookie.
        var cookie = new byte[24];
        cookie[4] = 0; // compatibleVersion (cookie offset 4 = byte 0 of post-frameLength)
        cookie[5] = (byte)bitDepth; // bit depth at cookie offset 5
        var configBody = Concat(new byte[4], cookie);
        var configBox = Box("alac", configBody);

        // Outer 'alac' sample entry: 28-byte AudioSampleEntry header then the config box.
        var sampleEntryHeader = new byte[28];
        // samplesize lives at body offset 18 (legacy value TagLib would report).
        sampleEntryHeader[18] = (byte)(legacySampleSize >> 8);
        sampleEntryHeader[19] = (byte)(legacySampleSize & 0xFF);
        var sampleEntryBody = Concat(sampleEntryHeader, configBox);
        var sampleEntry = Box("alac", sampleEntryBody);

        return WrapInContainers(sampleEntry);
    }

    private static byte[] BuildAacFile()
    {
        var sampleEntry = Box("mp4a", new byte[28]);
        return WrapInContainers(sampleEntry);
    }

    private static byte[] WrapInContainers(byte[] sampleEntry)
    {
        // stsd is a full box: 4 bytes version/flags + 4 bytes entry count precede the entries.
        var stsdBody = Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, sampleEntry);
        var stsd = Box("stsd", stsdBody);
        var stbl = Box("stbl", stsd);
        var minf = Box("minf", stbl);
        var mdia = Box("mdia", minf);
        var trak = Box("trak", mdia);
        var moov = Box("moov", trak);
        return moov;
    }

    private static byte[] Box(string type, byte[] content)
    {
        var size = 8 + content.Length;
        var box = new byte[size];
        box[0] = (byte)(size >> 24);
        box[1] = (byte)(size >> 16);
        box[2] = (byte)(size >> 8);
        box[3] = (byte)size;
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        content.CopyTo(box, 8);
        return box;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }
}
