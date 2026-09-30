using BardStage.Core.Rooms;
using Xunit;

namespace BardStage.Core.Tests;

public class LargeEnsembleTests
{
    [Fact] public void LocalSubsetKeepsCompleteTeamIdentityAndRejectsUnselectedReceiver()
    {
        var full = Plan(8);
        var subset = full with { LocalTeam = full.Song.Members, Song = full.Song with
            { Members = [1,2], Tracks = full.Song.Tracks[..2] } };
        var context = new LargeContext(2,full.Song.Members.Select(c=>new LargeMember(c,"",10,0)).ToArray(),100,10);
        subset.ValidateContext(context,1);
        Assert.Throws<InvalidOperationException>(()=>subset.ValidateContext(context with { SelfCid=3 },1));
        Assert.Throws<InvalidOperationException>(()=>subset.ValidateContext(context with { Members=context.Members[..2] },1));
        Assert.Throws<InvalidDataException>(()=>(subset with { LocalTeam=null }).Validate());
    }
    [Fact] public void LocalSubsetRejectsDuplicateMissingAndOversizeTeamIdentity()
    {
        var p = Plan(2);
        Assert.Throws<InvalidDataException>(()=>(p with { LocalTeam=[1,1] }).Validate());
        Assert.Throws<InvalidDataException>(()=>(p with { LocalTeam=[1,3] }).Validate());
        foreach (var count in new[] { 9, 16 })
            Assert.Throws<InvalidDataException>(()=>(p with { LocalTeam=Enumerable.Range(1,count).Select(i=>(ulong)i).ToArray() }).Validate());
    }
    [Fact] public void CompensationIsExplicitAndOldPlansRemainUncompensated()
    {
        var old = Plan();
        Assert.Equal(0, old.CompensationVersion);
        var compensated = old with { CompensationVersion = LargePlan.CurrentCompensationVersion };
        compensated.Validate();
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<LargePlan>(System.Text.Json.JsonSerializer.Serialize(compensated));
        Assert.Equal(1, roundTrip!.CompensationVersion);
        Assert.Throws<InvalidDataException>(() => (old with { CompensationVersion = -1 }).Validate());
        Assert.Throws<InvalidDataException>(() => (old with { CompensationVersion = 2 }).Validate());
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(old).Replace(",\"CompensationVersion\":0", "");
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<LargePlan>(legacyJson)!.CompensationVersion);
    }
    private static LargePlan Plan(int count = 8)
    {
        var members = Enumerable.Range(1,count).Select(i => (ulong)i).ToArray();
        var id = Guid.NewGuid();
        return new(id, LargeContext.RosterKey(members.Select(c => new LargeMember(c,"",10,0))),100,10,
            new(id,new string('A',64),0,1,members,members.Select((c,i) => new RoomTrackAssignment(i,true,2,0,c)).ToArray(),1,true,2));
    }
    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    public void PublicPlansAndContextsRejectMoreThanEightPerformers(int count)
    {
        var p = Plan(); p.Validate();
        p.Song.Validate();
        Assert.Equal(8, LargePlan.MaxPlayers);
        var over = Plan(count);
        Assert.Throws<InvalidDataException>(() => over.Validate());
        Assert.Throws<InvalidDataException>(() => over.Song.Validate());
        Assert.Throws<InvalidDataException>(() => over.Song.Validate(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => over.Song.Validate(count));
        var context = new LargeContext(1, over.Song.Members.Select(c => new LargeMember(c,"",10,0)).ToArray(),100,10);
        Assert.NotNull(context.Validate());
        Assert.Throws<InvalidOperationException>(() => p.ValidateContext(context,1));
        Assert.Throws<InvalidDataException>(() => Plan(1).Validate());
    }
    [Fact] public void ValidateRosterIdentitySceneAndUniquePerformers()
    {
        var p = Plan();
        var context = new LargeContext(8,p.Song.Members.Reverse().Select(c => new LargeMember(c,"x",10,1)).ToArray(),100,10);
        p.ValidateContext(context,1);
        Assert.Throws<InvalidOperationException>(() => p.ValidateContext(context,2));
        Assert.Throws<InvalidOperationException>(() => p.ValidateContext(context with { World = 11 },1));
        Assert.Throws<InvalidOperationException>(() => p.ValidateContext(context with { Territory = 101 },1));
        Assert.Throws<InvalidOperationException>(() => p.ValidateContext(context with { Members = context.Members[..7] },1));
        Assert.Throws<InvalidDataException>(() => (p with { Song = p.Song with { Members = [..p.Song.Members[..7],1] } }).Validate());
        Assert.Throws<InvalidDataException>(() => (p with { Song = p.Song with { Tracks = [new(0,true,2,0,99)] } }).Validate());
        Assert.Throws<InvalidDataException>(() => (p with { Song = p.Song with { Tracks = [new(0,true,2,0,1),new(1,true,20,0,1)] } }).Validate());
    }
    [Fact] public void ClockWorksWithDifferentUptimeAndNetworkLatency()
    {
        var sync = new EnsembleClock();
        for (var i=0;i<8;i++) Assert.True(sync.Add(i, i+1200+.015, i+1200+.018, i+.033));
        Assert.True(sync.Ready); Assert.InRange(sync.Offset,1199.9999,1200.0001);
        Assert.InRange(sync.Rtt,.0299,.0301); Assert.InRange(sync.Jitter,0,.000001);
    }
    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    public void TransportEnvelopesAndRoomAdvertisementsRejectOversizedGroups(int count)
    {
        var plan=Plan();
        var report=new LargeReport(1,Guid.NewGuid(),true,plan.Roster,100,10,Guid.Empty,Guid.Empty,
            false,false,false,false,true,0,0,0,"");
        new LargeEnvelope { Reports=Enumerable.Range(1,8).Select(i=>report with { Cid=(ulong)i }).ToArray() }.ValidateCapacity();
        Assert.Throws<InvalidDataException>(()=>new LargeEnvelope
            { Reports=Enumerable.Range(1,count).Select(i=>report with { Cid=(ulong)i }).ToArray() }.ValidateCapacity());
        Assert.Throws<InvalidDataException>(()=>new LargeEnvelope
            { LocalSessions=Enumerable.Range(1,count).ToDictionary(i=>(ulong)i,_=>Guid.NewGuid()) }.ValidateCapacity());
        Assert.Throws<InvalidDataException>(()=>new LargeEnvelope { Plan=Plan(count) }.ValidateCapacity());
        Assert.Throws<InvalidDataException>(()=>new LargeEnvelope { Plan=plan with
            { LocalTeam=Enumerable.Range(1,count).Select(i=>(ulong)i).ToArray() } }.ValidateCapacity());
        using var server=new RoomServer(0);
        server.Publish(new RoomSnapshot { RoomId=server.RoomId,LargeEnsembleSupported=true,PerformerCapacity=8 });
        Assert.Throws<InvalidDataException>(()=>server.Publish(new RoomSnapshot
            { RoomId=server.RoomId,LargeEnsembleSupported=true,PerformerCapacity=count }));
    }
    [Fact] public void ClockRejectsMalformedAndStaleSamplesAndExcessiveLatency()
    {
        var sync = new EnsembleClock();
        Assert.False(sync.Add(double.NaN,0,0,1));
        Assert.False(sync.Add(1,1,0,2));
        Assert.False(sync.Add(0,1,1,3));
        Assert.False(sync.Add(0,1,2,.1));
        for (var i=0;i<6;i++) sync.Add(i,i+.3,i+.31,i+.61);
        Assert.False(sync.Ready);
        sync.Reset(); Assert.False(sync.Ready);
    }
    [Fact] public void ClockRejectsUnstableOffsets()
    {
        var sync = new EnsembleClock();
        for (var i=0;i<6;i++) sync.Add(i,i+(i%2==0?1:1.1),i+(i%2==0?1:1.1),i+.1);
        Assert.False(sync.Ready);
    }
    [Fact] public void PublicTransportCannotOptIntoSixteenPerformerCapacity()
    {
        using var standard = new RoomServer(0);
        using var explicitEight = new RoomServer(0,7);
        Assert.Equal(7,standard.ViewerCapacity); Assert.Equal(7,explicitEight.ViewerCapacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomServer(0,8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomServer(0,15));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomServer(0,23));
    }
}
