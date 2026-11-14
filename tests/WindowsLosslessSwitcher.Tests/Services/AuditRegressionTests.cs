using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;
using WindowsLosslessSwitcher.Models;
using WindowsLosslessSwitcher.Services;
using Xunit;

namespace WindowsLosslessSwitcher.Tests.Services;

public sealed class AuditRegressionTests
{
    [Fact]
    public void Plist_RejectsCyclesAndOutOfBoundsReferences()
    {
        var reader = new BinaryPlistReader();
        Assert.Throws<InvalidDataException>(() => reader.ReadRootDictionary(Plist([0xd1, 1, 0], [0x51, (byte)'k'])));
        Assert.Throws<InvalidDataException>(() => reader.ReadRootDictionary(Plist([0xd1, 1, 2], [0x51, (byte)'k'])));
        Assert.Throws<InvalidDataException>(() => reader.ReadRootDictionary(Plist([0xdf, 0x13, 0x7f, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff])));
    }

    [Fact]
    public void Plist_ReadsValidDictionaryAndSharedReferences()
    {
        var result = new BinaryPlistReader().ReadRootDictionary(Plist([0xd1, 1, 2], [0x51, (byte)'k'], [0xa2, 3, 3], [0x10, 42]));
        Assert.Equal(new object?[] { 42L, 42L }, Assert.IsType<List<object?>>(result["k"]));
        result = new BinaryPlistReader().ReadRootDictionary(Plist([0xd1, 1, 2], [0x51, (byte)'k'],
            [0x13, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff]));
        Assert.Equal(-1L, result["k"]);
    }

    [Fact]
    public void Plist_CanReadWhileWriterHasFileOpen()
    {
        InTemporaryDirectory(directory =>
        {
            var path = Path.Combine(directory, "preferences.plist");
            File.WriteAllBytes(path, Plist([0xd0]));
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert.Empty(new BinaryPlistReader().ReadRootDictionary(path));
        });
    }

    [Fact]
    public void Settings_FailedReplacementPreservesPreviousFileAndCleansTemporaryFile()
    {
        InTemporaryDirectory(directory =>
        {
            var service = new SettingsService(directory);
            service.Save(new AppSettings { DefaultBitDepth = 16 });
            using (var reader = new FileStream(service.GetSettingsPath(), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => service.Save(new AppSettings { DefaultBitDepth = 24 }));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal(16, service.Load().DefaultBitDepth);
            Assert.Single(Directory.GetFiles(directory));
        });
    }

    [Fact]
    public void Settings_SnapshotDoesNotShareMutableLists()
    {
        var original = new AppSettings { AllowedSampleRates = [44100], AllowedBitDepths = [24],
            TrackFormatCorrections = [new("Title", "Artist", "", 44100, 24)] };
        var copy = original.Snapshot();
        original.AllowedSampleRates.Clear();
        original.AllowedBitDepths.Clear();
        original.TrackFormatCorrections.Clear();
        Assert.Equal([44100], copy.AllowedSampleRates);
        Assert.Equal([24], copy.AllowedBitDepths);
        Assert.Single(copy.TrackFormatCorrections);
    }

    [Theory]
    [InlineData("AC/DC", "AC/DC")]
    [InlineData("Earth, Wind & Fire", "Earth, Wind & Fire")]
    [InlineData("First / Second feat. Third", "First")]
    public void PrimaryArtist_PreservesBandNamesAndUsesEarliestExplicitSeparator(string artist, string expected)
        => Assert.Equal(expected, AppleMusicTrackMetadataNormalizer.Normalize("Song", artist, "Album").PrimaryArtist);

    [Fact]
    public void Corrections_MatchMissingAlbumOnlyToMissingAlbumAndRejectPunctuationIdentity()
    {
        var track = new TrackSnapshot("AppleMusic.exe", null, "Song", "Artist", "", "test", DateTimeOffset.UtcNow);
        var correction = TrackFormatCorrection.FromTrack(track, 44100, 24);
        Assert.True(correction.Matches(track));
        Assert.False(correction.Matches(track with { Album = "Album" }));
        Assert.False(new TrackFormatCorrection("!!!", "Artist", "", 44100, 24).IsValid);
        Assert.False(new TrackFormatCorrection("Song", "Artist", "!!!", 44100, 24).Matches(track with { Album = "???" }));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Probe_IncludesPositionedIntegerPcm(int depth)
    {
        var format = EndpointFormatSupport.CreateProbeFormats(48000, depth, 2).First(f => f is WaveFormatExtensible &&
            new Guid(EndpointFormatSupport.SerializeWaveFormat(f).AsSpan(24, 16)) == new Guid("00000001-0000-0010-8000-00AA00389B71"));
        var blob = EndpointFormatSupport.SerializeWaveFormat(format);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(20)));
        Assert.Equal(new Guid("00000001-0000-0010-8000-00AA00389B71"), new Guid(blob.AsSpan(24, 16)));
    }

    [Theory]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2", true)]
    [InlineData("1.0.0-beta.2+abc", "1.0.0-beta.2+def", true)]
    [InlineData("1.0.0-beta", "1.0.0-beta.1", false)]
    [InlineData("1.0.0-beta.1", "1.0.0-beta", true)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.x", false)]
    public void Updates_OrderPrereleasesAndIgnoreBuildMetadata(string current, string release, bool expected)
        => Assert.Equal(expected, AppUpdater.IsPortableUpToDate(AppUpdater.ParseVersion(current), current, AppUpdater.ParseVersion(release)!, release));

    [Fact]
    public void Logger_RotatesLargeLogAndAllowsRepeatedDisposal()
    {
        InTemporaryDirectory(directory =>
        {
            using var logger = new DiagnosticsLogger(directory);
            File.WriteAllText(logger.LogPath, new string('x', 5 * 1024 * 1024));
            logger.Info("after rotation");
            Assert.Contains("after rotation", File.ReadAllText(logger.LogPath));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(logger.LogPath)!, "switcher.old.log")));
            logger.Dispose();
            logger.Info("late callback");
        });
    }

    private static byte[] Plist(params byte[][] objects)
    {
        using var stream = new MemoryStream();
        stream.Write("bplist00"u8);
        var offsets = new List<byte>();
        foreach (var obj in objects) { offsets.Add(checked((byte)stream.Position)); stream.Write(obj); }
        var tableOffset = stream.Position;
        stream.Write(offsets.ToArray());
        var trailer = new byte[32];
        trailer[6] = 1; trailer[7] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)objects.Length);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)tableOffset);
        stream.Write(trailer);
        return stream.ToArray();
    }

    private static void InTemporaryDirectory(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WlsAudit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
