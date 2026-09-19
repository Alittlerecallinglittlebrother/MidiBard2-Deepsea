// Copyright (C) 2022 akira0245
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see https://github.com/akira0245/MidiBard/blob/master/LICENSE.
//
// This code is written by akira0245 and was originally used in the MidiBard project. Any usage of this code must prominently credit the author, akira0245, and indicate that it was originally used in the MidiBard project.

using System;
using System.Linq;

using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using static Dalamud.api;

namespace MidiBard.Managers;

public class PartyWatcher : IDisposable
{
    public PartyWatcher()
    {
        api.Framework.Update += Framework_Update;
    }

    public ulong[] PartyMemberCIDs { get; private set; } = Array.Empty<ulong>();
    private static ulong[] displayedMemberCids = Array.Empty<ulong>();

    internal static ulong[] GetDisplayedMemberCIDs() => displayedMemberCids.ToArray();

    private static unsafe void CaptureDisplayedOrder()
    {
        var party = api.PartyList.Select(p => p.ContentId).Where(cid => cid != 0).ToHashSet();
        if (party.Count < 2) { displayedMemberCids = Array.Empty<ulong>(); return; }
        var hud = AgentHUD.Instance();
        if (hud == null) { displayedMemberCids = Array.Empty<ulong>(); return; }
        var rows = new System.Collections.Generic.List<(int Slot, ulong Cid)>();
        foreach (var member in hud->PartyMembers)
            if (party.Contains(member.ContentId)) rows.Add((member.Index, member.ContentId));
        displayedMemberCids = rows.Count == party.Count && rows.Select(p => p.Cid).Distinct().Count() == party.Count
            && rows.Select(p => p.Slot).Distinct().Count() == party.Count
            ? rows.OrderBy(p => p.Slot).Select(p => p.Cid).ToArray() : Array.Empty<ulong>();
    }

    public static ulong[] GetMemberCIDs()
    {
        System.Collections.Generic.List<ulong> cids = new();
        foreach (var p in api.PartyList)
        {
            try
            {
                if (p.EntityId <= 0 || !p.GameObject.IsValid())
                    continue;
                if (p.World.Value.RowId > 0 && p.Territory.Value.RowId > 0)
                {
                    cids.Add(p.ContentId);
                }
            }
            catch (NullReferenceException) { }
        }
        return cids.ToArray();
    }

    private void Framework_Update(IFramework framework)
    {
        CaptureDisplayedOrder();
        var newMemberCIDs = GetMemberCIDs();
        if (!newMemberCIDs.ToHashSet().SetEquals(PartyMemberCIDs.ToHashSet()))
        {
            //PluginLog.Warning($"CHANGE {newList.Length - PartyMembers.Length}");
            //PluginLog.Information("OLD:\n"+string.Join("\n", PartyMembers.Select(i=>$"{i.Name} {i.ContentId:X}")));
            //PluginLog.Information("NEW:\n"+string.Join("\n", newList.Select(i=>$"{i.Name} {i.ContentId:X}")));

            foreach (var cid in newMemberCIDs)
            {
                if (!PartyMemberCIDs.Any(i => i == cid))
                {
                    PluginLog.Debug($"JOIN {cid}");
                    PartyMemberJoin?.Invoke(this, cid);
                }
            }

            foreach (var partyMember in PartyMemberCIDs)
            {
                if (!newMemberCIDs.Any(i => i == partyMember))
                {
                    PluginLog.Debug($"LEAVE {partyMember}");
                    PartyMemberLeave?.Invoke(this, partyMember);
                }
            }
        }

        PartyMemberCIDs = newMemberCIDs;
    }

    public static event EventHandler<ulong> PartyMemberJoin;
    public static event EventHandler<ulong> PartyMemberLeave;

    public void Dispose()
    {
        api.Framework.Update -= Framework_Update;
        PartyMemberJoin = delegate { };
        PartyMemberLeave = delegate { };
    }
}
