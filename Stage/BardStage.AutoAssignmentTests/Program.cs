using MidiBard.Managers;

var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException(label);
    checks++;
    Console.WriteLine("PASS: " + label);
}

var names = new (string Name, uint Id)[]
{
    ("01 萨克斯 主旋律", 19), ("02 Alto Sax", 19), ("SAXOPHONE+1", 19),
    ("F-ShuangHuangGuan", 6), ("M-DanHuangGuan", 7), ("GangQin", 2), ("ShuQin", 1),
    ("DaTiQin", 22), ("DiYinGu", 12), ("XiaoJunGu", 13), ("Cha", 14),
    ("08-Cha+1", 14), ("ChangDi", 5), ("ChangHao", 16),
    ("Electric Guitar Clean", 25), ("Electric_Guitar_Muted", 26),
    ("Electric Guitar Power Chords", 27), ("Program: ElectricGuitarSpecial", 28),
    ("Program: ElectricGuitarClean", 25), ("Pan Flute", 9), ("Double Bass", 23),
    ("小提琴1", 20), ("中提琴", 21), ("大提琴", 22), ("低音提琴", 23),
    ("Pizzicato", 4), ("定音鼓", 10), ("邦戈鼓", 11), ("法國號 French Horn", 18),
};
foreach (var sample in names)
    Check(AutomaticEnsembleRules.InstrumentFromName(sample.Name) == sample.Id, "instrument " + sample.Name);
Check(AutomaticEnsembleRules.InstrumentFromName("Channel 1") == null, "short pinyin does not match Channel");
Check(AutomaticEnsembleRules.InstrumentFromName(null) == null, "missing names do not throw");
Check(AutomaticEnsembleRules.InstrumentFromName("未知音轨") == null, "unknown names retain fallback choice");
Check(AutomaticEnsembleRules.InstrumentFromProgram(65) == 19, "GM alto sax fallback");
Check(AutomaticEnsembleRules.InstrumentFromProgram(40) == 20, "GM violin fallback");
Check(AutomaticEnsembleRules.InstrumentFromProgram(127) == null, "unsupported GM program is not guessed");
Check(AutomaticEnsembleRules.ResolveInstrument("Sax", 2, 40) == 19, "explicit track name overrides stale sidecar instrument and GM program");
Check(AutomaticEnsembleRules.ResolveInstrument("Untitled", 22, 40) == 22, "unknown name preserves existing valid instrument");
Check(AutomaticEnsembleRules.ResolveInstrument("Untitled", 0, 40) == 20, "invalid saved instrument falls back to embedded GM program");
Check(AutomaticEnsembleRules.ResolveInstrument("Untitled", null, null) == 1, "unknown instrument uses playable harp fallback");

ulong[] order = [23, 51, 12, 91, 66, 87, 45, 32];
Check(AutomaticEnsembleRules.AssignTracks(8, order).SequenceEqual(order), "eight note tracks follow leader UI slots without CID sorting");
Check(AutomaticEnsembleRules.AssignTracks(2, order).SequenceEqual(order.Take(2)), "duet assigns only first two players");
Check(!AutomaticEnsembleRules.AssignTracks(2, order).Contains(order[2]), "unassigned third member has no track");
Check(AutomaticEnsembleRules.AssignTracks(10, order).Skip(8).All(cid => cid == 0), "excess note tracks remain unassigned");
Check(AutomaticEnsembleRules.AssignTracks(8, order.Take(2).ToArray()).Skip(2).All(cid => cid == 0), "missing party slots are not duplicated");
Check(AutomaticEnsembleRules.AssignTracks(3, new ulong[] { 11, 0, 33 }).SequenceEqual(new ulong[] { 11, 0, 33 }), "empty slot does not shift later tracks");
Check(AutomaticEnsembleRules.AssignTracks(3, new ulong[] { 11, 11, 33 }).SequenceEqual(new ulong[] { 11, 0, 33 }), "duplicate CID never receives two automatic tracks");
Check(AutomaticEnsembleRules.AssignTracks(8, Array.Empty<ulong>()).All(cid => cid == 0), "no authoritative order leaves clients silent");

var token = AutomaticEnsembleRules.EncodeOrder(order);
Check(AutomaticEnsembleRules.TryDecodeOrder(token, order.Reverse().ToArray(), out var received) && received.SequenceEqual(order), "different local party order preserves leader order on receive");
Check(!AutomaticEnsembleRules.TryDecodeOrder("auto=17,17,C,5B,42,57,2D,20", order, out _), "reject duplicate party IDs");
Check(!AutomaticEnsembleRules.TryDecodeOrder("auto=17,33,C,5B,42,57,2D,FFFF", order, out _), "reject stranger party ID");
Check(!AutomaticEnsembleRules.TryDecodeOrder(token, order.Take(7).ToArray(), out _), "reject stale membership snapshot");
Check(!AutomaticEnsembleRules.TryDecodeOrder("auto=nope", order, out _), "reject malformed order without exceptions");
Check(!AutomaticEnsembleRules.TryDecodeOrder("auto=0", new ulong[] { 0 }, out _), "reject zero CID");
Check(AutomaticEnsembleRules.EncodeOrder(Enumerable.Range(0, 8).Select(i => ulong.MaxValue - (ulong)i).ToArray()).Length <= 141, "full eight-person order fits party command");
Console.WriteLine($"Automatic ensemble assignment: {checks} checks passed.");
