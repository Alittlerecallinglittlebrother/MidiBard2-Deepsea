using System.Security.Cryptography;
using Melanchall.DryWetMidi.Core;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class MidiImportCompatibilityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "BardStage-midi-compatibility-" + Guid.NewGuid().ToString("N"));

    public MidiImportCompatibilityTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(7, 255)]
    [InlineData(255, 80)]
    public void OutOfRangeControlChangeImportsNotesWithoutRewritingOriginal(byte controller, byte value)
    {
        var path = WriteMidi("invalid-control-change.mid", controller, value);
        var original = File.ReadAllBytes(path);
        var lastWrite = File.GetLastWriteTimeUtc(path);
        // The fixture must reproduce the user's strict-reader failure, not merely contain a valid MIDI.
        Assert.ThrowsAny<Exception>(() => MidiFile.Read(path));
        var state = new CatalogState();

        var report = new MidiLibraryImporter().Import([path], state);

        Assert.Equal(1, report.Added);
        Assert.Empty(report.Errors);
        var song = Assert.Single(state.Songs);
        Assert.Equal(.5, song.DurationSeconds, precision: 5);
        Assert.Equal(1, song.TrackCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), song.Sha256);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void UnreadableFileDoesNotPreventValidAndCompatibleFilesBeingImported()
    {
        var good = WriteMidi("good.mid", 7, 80);
        var compatible = WriteMidi("invalid-control-change.mid", 7, 255);
        var corrupt = Path.Combine(directory, "unreadable.mid");
        File.WriteAllText(corrupt, "not a MIDI file");
        var state = new CatalogState();

        var report = new MidiLibraryImporter().Import([corrupt, good, compatible], state);

        Assert.Equal(2, report.Added);
        Assert.Equal(2, state.Songs.Count);
        Assert.Contains(corrupt, Assert.Single(report.Errors));
        Assert.Equal("not a MIDI file", File.ReadAllText(corrupt));
        Assert.DoesNotContain(state.Songs, song => song.FilePath == corrupt);
    }

    [Fact]
    public void CompatibleFileStillDeduplicatesUsingOriginalBytes()
    {
        var original = WriteMidi("invalid-control-change.mid", 7, 255);
        var copy = Path.Combine(directory, "copy.mid");
        File.Copy(original, copy);
        var state = new CatalogState();

        var report = new MidiLibraryImporter().Import([original, copy], state);

        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Duplicates);
        Assert.Empty(report.Errors);
        Assert.Single(state.Songs);
    }

    private string WriteMidi(string name, byte controller, byte value)
    {
        var path = Path.Combine(directory, name);
        // Format 0, one track, 480 PPQ. CC followed by one half-second C4 note and end-of-track.
        byte[] bytes = [
            0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 1, 0xE0,
            0x4D, 0x54, 0x72, 0x6B, 0, 0, 0, 17,
            0, 0xB0, controller, value,
            0, 0x90, 60, 80,
            0x83, 0x60, 0x80, 60, 0,
            0, 0xFF, 0x2F, 0];
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
