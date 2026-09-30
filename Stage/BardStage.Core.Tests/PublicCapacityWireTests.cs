using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using BardStage.Core.Rooms;
using Xunit;

namespace BardStage.Core.Tests;

public sealed class PublicCapacityWireTests
{
    [Theory]
    [InlineData(9)]
    [InlineData(16)]
    public async Task ProductionTlsFrameReaderAndWriterRejectOversizedLegacyPackets(int count)
    {
        var valid=Plan(8);
        var report=new LargeReport(1,Guid.NewGuid(),true,valid.Roster,100,10,Guid.Empty,Guid.Empty,
            false,false,false,false,true,0,0,0,"");
        var cases=new LargeEnvelope[]
        {
            new() { Plan=Plan(count) },
            new() { Plan=valid with { LocalTeam=Enumerable.Range(1,count).Select(i=>(ulong)i).ToArray() } },
            new() { Reports=Enumerable.Range(1,count).Select(i=>report with { Cid=(ulong)i }).ToArray() },
            new() { LocalSessions=Enumerable.Range(1,count).ToDictionary(i=>(ulong)i,_=>Guid.NewGuid()) }
        };
        foreach(var envelope in cases)
        {
            var packet=new RoomPacket { Type="largeFrame",Large=envelope };
            using var outbound=new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(()=>Write(outbound,packet));
            Assert.Equal(0,outbound.Length);
            // A legacy sender can serialize directly, bypassing this build's Write guard.
            // Feed its actual big-endian frame into the production TLS RoomWire reader.
            using var legacy=LegacyFrame(packet);
            await Assert.ThrowsAsync<InvalidDataException>(()=>Read(legacy));
        }
        var goodPacket=new RoomPacket { Type="largeFrame",Large=new() { Plan=valid } };
        using var validOutbound=new MemoryStream();
        await Write(validOutbound,goodPacket);
        Assert.True(validOutbound.Length>4);
        validOutbound.Position=0;
        var roundTrip=await Read(validOutbound);
        Assert.Equal(valid.Id,roundTrip.Large!.Plan!.Id);
        Assert.Equal(8,roundTrip.Large.Plan.Song.Members.Length);
        using var independentLegacy=LegacyFrame(goodPacket);
        Assert.Equal(8,(await Read(independentLegacy)).Large!.Plan!.Song.Members.Length);
    }

    private static LargePlan Plan(int count)
    {
        var id=Guid.NewGuid();
        var members=Enumerable.Range(1,count).Select(i=>(ulong)i).ToArray();
        return new(id,LargeContext.RosterKey(members.Select(cid=>new LargeMember(cid,"",10,0))),100,10,
            new(id,new string('A',64),0,1,members,members.Select((cid,i)=>new RoomTrackAssignment(i,true,2,0,cid)).ToArray(),1,true,2));
    }
    private static MemoryStream LegacyFrame(RoomPacket packet)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(packet,RoomJson.Options);
        var frame=new MemoryStream();
        var header=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header,bytes.Length);
        frame.Write(header); frame.Write(bytes); frame.Position=0;
        return frame;
    }
    private static readonly Type Wire=typeof(RoomServer).Assembly.GetType("BardStage.Core.Rooms.RoomWire",throwOnError:true)!;
    private static Task<RoomPacket> Read(Stream stream)=>(Task<RoomPacket>)Wire.GetMethod("Read",BindingFlags.Static|BindingFlags.Public)!
        .Invoke(null,[stream,CancellationToken.None,8*1024*1024,null])!;
    private static Task Write(Stream stream,RoomPacket packet)=>(Task)Wire.GetMethod("Write",BindingFlags.Static|BindingFlags.Public)!
        .Invoke(null,[stream,packet,CancellationToken.None])!;
}
