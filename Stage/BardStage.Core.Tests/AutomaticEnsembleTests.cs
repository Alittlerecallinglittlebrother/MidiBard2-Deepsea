using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using MidiBard.Managers;
using Xunit;

public sealed class AutomaticEnsembleTests
{
    [Theory]
    [InlineData("萨克斯", 19)]
    [InlineData("Sax", 19)]
    [InlineData("01 Saxophone +12", 19)]
    [InlineData("F-ShuangHuangGuan", 6)]
    [InlineData("M-DanHuangGuan", 7)]
    [InlineData("GangQin", 2)]
    [InlineData("钢琴", 2)]
    [InlineData("Electric Guitar Clean", 25)]
    [InlineData("Program:ElectricGuitarMuted", 26)]
    [InlineData("Double Bass", 23)]
    [InlineData("小军鼓", 13)]
    [InlineData("Cha", 14)]
    [InlineData("ChangDi", 5)]
    public void NamesResolveToGameInstruments(string name, int id) =>
        Assert.Equal((uint)id, AutomaticEnsembleRules.InstrumentFromName(name));

    [Fact]
    public void ExplicitNameThenSavedThenGmThenHarp()
    {
        Assert.Equal(19u, AutomaticEnsembleRules.ResolveInstrument("Sax", 2, 73));
        Assert.Equal(2u, AutomaticEnsembleRules.ResolveInstrument("Track 1", 2, 73));
        Assert.Equal(5u, AutomaticEnsembleRules.ResolveInstrument("Track 1", 0, 73));
        Assert.Equal(1u, AutomaticEnsembleRules.ResolveInstrument("Track 1", null, null));
        Assert.Null(AutomaticEnsembleRules.InstrumentFromName("Channel 1"));
    }

    [Fact]
    public void TwoNoteTracksSkipConductorAndEmptyTracksAndOnlyUseFirstTwoMembers()
    {
        var sax = new TrackChunk(new SequenceTrackNameEvent("萨克斯"), new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90));
        var piano = new TrackChunk(new SequenceTrackNameEvent("Piano"), new NoteOnEvent((SevenBitNumber)64, (SevenBitNumber)90));
        var file = new MidiFile(new TrackChunk(new SetTempoEvent()), sax,
            new TrackChunk(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)0)), piano, new TrackChunk());
        var noteTracks = AutomaticEnsembleRules.NoteTracks(file).ToArray();
        Assert.Equal(new[] { sax, piano }, noteTracks);
        var party = new ulong[] { 80, 10, 40, 20, 30, 60, 70, 50 };
        Assert.Equal(new ulong[] { 80, 10 }, AutomaticEnsembleRules.AssignTracks(noteTracks.Length, party));
        Assert.Equal(party, AutomaticEnsembleRules.AssignTracks(8, party));
        Assert.Equal(new ulong[] { 80, 10, 0 }, AutomaticEnsembleRules.AssignTracks(3, party.Take(2).ToArray()));
        Assert.Equal(new ulong[] { 10, 80 }, AutomaticEnsembleRules.AssignTracks(2, new ulong[] { 10, 80 }));
    }

    [Fact]
    public void OrderTokenRequiresExactCurrentPartyWithoutDuplicates()
    {
        ulong[] party = [33, 11, 22];
        Assert.True(AutomaticEnsembleRules.TryDecodeOrder(AutomaticEnsembleRules.EncodeOrder(party), [11, 22, 33], out var decoded));
        Assert.Equal(party, decoded);
        foreach (var token in new[] { "auto=21,B,B", "auto=21,B", "auto=21,B,FF", "auto=0,B,16", "auto=GG,B,16", "garbage" })
            Assert.False(AutomaticEnsembleRules.TryDecodeOrder(token, party, out _));
    }
}
