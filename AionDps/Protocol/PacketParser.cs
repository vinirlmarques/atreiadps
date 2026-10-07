using System.Buffers;
using K4os.Compression.LZ4;

namespace AionDps.Protocol;

// ---------------------------------------------------------------- eventos

public abstract record GameEvent(long TimeMs);
/// <param name="Scalar">Escalar de poder do autor (1000..200000; 0 = desconhecido). Invocações carregam o do dono.</param>
public sealed record DamageEvent(long TimeMs, uint Actor, uint Target, int Skill, long Amount, bool Crit, bool Dot, bool Back, int Hits,
    int Scalar = 0) : GameEvent(TimeMs);
public sealed record SelfEvent(long TimeMs, uint Id, string Name, GameClass Class, int Server = 0) : GameEvent(TimeMs);
public sealed record PlayerEvent(long TimeMs, uint Id, string Name, GameClass Class) : GameEvent(TimeMs);
public sealed record SummonEvent(long TimeMs, uint Id, uint OwnerId, string? OwnerName) : GameEvent(TimeMs);
public sealed record NpcEvent(long TimeMs, uint Id, int Code, long MaxHp) : GameEvent(TimeMs);
public sealed record HpEvent(long TimeMs, uint Id, long Hp, long MaxHp) : GameEvent(TimeMs);
public sealed record ZoneEvent(long TimeMs, int MapId) : GameEvent(TimeMs);
/// <summary>Status com formato bit 0 só são enviados para o seu próprio personagem.</summary>
public sealed record SelfStatsEvent(long TimeMs, uint Id) : GameEvent(TimeMs);
/// <param name="CharacterId">32 bits baixos do id do personagem (o mesmo que o pacote <c>20 36</c> liga a uma entidade).</param>
public sealed record PartyMember(string Name, GameClass Class, int Level, uint? GearScore, ulong? CombatPower,
    uint CharacterId = 0, int Server = 0);
/// <summary><c>20 36</c>: liga a entidade da sessão ao id global do personagem.</summary>
public sealed record CharacterLinkEvent(long TimeMs, uint Id, uint CharacterId) : GameEvent(TimeMs);
public sealed record PartyEvent(long TimeMs, List<PartyMember> Members) : GameEvent(TimeMs);

public enum GameClass { Unknown, Gladiator, Templar, Assassin, Ranger, Sorcerer, Elementalist, Cleric, Chanter, Brawler }

/// <summary>
/// Decodifica pacotes (opcode + corpo, servidor→cliente) em eventos. Layouts do cliente pós-junho/2026 (Global).
/// Opcodes no formato 0xAABB, onde AA é o primeiro byte no fio.
/// </summary>
public sealed class PacketParser(Action<GameEvent> sink)
{
    public const ushort OpHeartbeat = 0x0036;
    public const ushort OpSelfInfo = 0x3336;
    public const ushort OpPlayerInfo = 0x4536;
    public const ushort OpPlayerInfoOld = 0x4436;
    public const ushort OpSpawn = 0x4136;
    public const ushort OpMapLoad = 0x2136;
    public const ushort OpDamage = 0x0438;
    public const ushort OpDot = 0x0538;
    public const ushort OpEntityStats = 0x008D;
    public const ushort OpHpMp = 0x1B92;
    public const ushort OpPartyRoster = 0x0297;
    public const ushort OpCharacterLink = 0x2036;

    public long TimeMs { get; set; }
    public long Packets { get; private set; }
    public long DamagePackets { get; private set; }

    /// <summary>Opcional: ids de skills de DoT que são dano (o resto do 05 38 é buff/debuff sem dano).</summary>
    public HashSet<int> DotSkills { get; } = new();

    /// <summary>Opcional: recebe todo pacote (diagnóstico).</summary>
    public Action<ushort, byte[]>? Tap { get; set; }

    public void Handle(ushort opcode, ReadOnlySpan<byte> body)
    {
        Packets++;
        Tap?.Invoke(opcode, body.ToArray());
        switch (opcode)
        {
            case OpHeartbeat: break;
            case OpDamage: ParseDamage(body); break;
            case OpDot: ParseDot(body); break;
            case OpSelfInfo:
                ParseIdentity(body, 0, isSelf: true);
                ScanEmbeddedBundles(body);
                break;
            case OpPlayerInfo:
            case OpPlayerInfoOld:
                ParseIdentity(body, 0, isSelf: false);
                ScanEmbeddedBundles(body);
                break;
            case OpSpawn: ParseSpawn(body); break;
            case OpMapLoad: ParseMapLoad(body); break;
            case OpEntityStats: ParseEntityStats(body); break;
            case OpHpMp: ParseHpMp(body); break;
            case OpPartyRoster: ParsePartyRoster(body); break;
            case OpCharacterLink: ParseCharacterLink(body); break;
            default:
                // Registros de identidade também vêm embutidos em outros pacotes (e em bundles LZ4 dentro deles).
                if (body.Length >= 16)
                {
                    ScanForIdentities(body);
                    ScanEmbeddedBundles(body);
                }
                break;
        }
    }

    // ------------------------------------------------------------ dano (04 38)

    private void ParseDamage(ReadOnlySpan<byte> b)
    {
        var o = 0;
        var first = true;
        while (o < b.Length)
        {
            if (o + 1 < b.Length && b[o] == 0x01 && b[o + 1] == 0x00) o += 2; // marcador de registro encadeado
            else if (!first) break;

            // switch & 0x0F == 0 => anúncio de cast, sem dano
            if (first && IsCastMarker(b, o)) return;
            if (!TryDamageRecord(b, ref o, out var ev, out var clean)) break;
            DamagePackets++;
            if (ev is not null) sink(ev);
            first = false;
            if (!clean) break;
        }
    }

    private static bool IsCastMarker(ReadOnlySpan<byte> b, int o) =>
        Wire.TryVarint(b, ref o, out _) && Wire.TryVarint(b, ref o, out var sw) && (sw & 0x0F) == 0;

    // tamanho do bloco variável por layout (switch & 0x0F)
    private static readonly int[][] BlockCandidates = [[], [], [], [], [8], [11, 12], [11, 10], [11, 14]];

    /// <summary>
    /// <code>
    /// alvo varint, switch varint (layout = switch &amp; 0x0F ∈ 4..7; 0x20 = hits adicionais), flag varint,
    /// autor varint, skill u32, uid u8, tipoDano varint (3 = crítico), bloco (depende do layout),
    /// [0 pad] escalar varint, dano varint, cauda
    /// </code>
    /// </summary>
    private bool TryDamageRecord(ReadOnlySpan<byte> b, ref int o, out DamageEvent? ev, out bool cleanTail)
    {
        ev = null;
        cleanTail = false;
        if (!Wire.TryVarint(b, ref o, out var target) || !IsEntityId(target)) return false;
        if (!Wire.TryVarint(b, ref o, out var sw)) return false;
        var layout = (int)(sw & 0x0F);
        if (layout is < 4 or > 7) return false;
        if (!Wire.TryVarint(b, ref o, out _)) return false;
        if (!Wire.TryVarint(b, ref o, out var actor) || !IsEntityId(actor)) return false;
        if (!Wire.TryU32(b, ref o, out var rawSkill)) return false;
        var skill = (long)rawSkill;
        if (skill is >= 3_000_000 and <= 3_099_999) skill = skill * 10 + 1; // Theostones
        if (skill is < 1 or > 299_999_999) return false;
        if (!Wire.TryU8(b, ref o, out _)) return false; // uid do hit
        if (!Wire.TryVarint(b, ref o, out var dmgType)) return false;

        var crit = dmgType == 3;
        var back = false;
        if (layout >= 5 && o + 2 < b.Length) back = b[o + 2] == 0x01;

        var blockStart = o;
        (int End, uint Damage, uint Scalar)? chosen = null;
        foreach (var blockLen in BlockCandidates[layout])
        {
            if (!TryValues(b, blockStart + blockLen, layout, dmgType, out var end, out var scalar, out var damage)) continue;
            if (damage is < 1 or > 99_999_999) continue;
            if (scalar == 0 || scalar is >= 1_000 and <= 200_000)
            {
                chosen = (end, damage, scalar);
                break;
            }
            chosen ??= (end, damage, 0);
        }
        if (chosen is not { } c) return false;
        o = c.End;

        var hits = 1;
        if (TryTail(b, o, layout, sw, c.Damage, out var tailEnd, out var extra) ||
            (layout != 6 && TryTail(b, o, 6, sw, c.Damage, out tailEnd, out extra)))
        {
            o = tailEnd;
            hits += extra;
            cleanTail = true;
        }
        else if ((sw & 0x20) != 0 && Wire.TryVarint(b, o, out var count, out _) && count is >= 1 and <= 25)
        {
            hits += (int)count;
        }

        if (cleanTail && o + 1 < b.Length && b[o] == 0x03 && b[o + 1] == 0x00)
        {
            o += 2; // sufixo de roubo de vida
            Wire.TryVarint(b, ref o, out _);
        }

        if (actor == target) return true; // auto-cura, não é dano
        ev = new DamageEvent(TimeMs, actor, target, NormalizeSkill((int)skill), c.Damage, crit, false, back, hits, (int)c.Scalar);
        return true;
    }

    private static bool TryValues(ReadOnlySpan<byte> b, int o, int layout, uint dmgType, out int end, out uint scalar, out uint damage)
    {
        end = 0;
        scalar = damage = 0;
        if (o >= b.Length) return false;
        if (!Wire.TryVarint(b, ref o, out var v1)) return false;
        var afterV1 = o;
        if (!Wire.TryVarint(b, ref o, out var v2)) return false;
        if (v1 == 0)
        {
            v1 = v2;
            afterV1 = o;
            if (!Wire.TryVarint(b, ref o, out v2)) return false;
        }
        // Alguns críticos de layout 6 trazem o dano primeiro e sem escalar.
        if (layout == 6 && dmgType == 3 && v1 is >= 1_000 and <= 5_000_000 && v2 <= 25)
        {
            damage = v1;
            end = afterV1;
            return true;
        }
        scalar = v1;
        damage = v2;
        end = o;
        return true;
    }

    private static bool TryTail(ReadOnlySpan<byte> b, int o, int layout, uint sw, uint damage, out int end, out int extraHits)
    {
        end = o;
        extraHits = 0;
        if (layout == 4 && (!Wire.TryVarint(b, ref o, out var field) || field is < 1 or > 25)) return false;
        if ((sw & 0x20) != 0)
        {
            if (!Wire.TryVarint(b, ref o, out var count) || count is < 1 or > 25) return false;
            long sum = 0;
            for (var i = 0; i < count; i++)
            {
                if (!Wire.TryVarint(b, ref o, out var hit)) return false;
                sum += hit;
            }
            if (sum >= damage) return false;
            extraHits = (int)count;
        }
        var rest = b[o..];
        var clean = rest.IsEmpty
                    || (rest.Length >= 2 && rest[1] == 0x00 && rest[0] is >= 1 and <= 7)
                    || (rest.Length >= 2 && rest[0] == 0x04 && rest[1] == 0x38);
        if (!clean) return false;
        end = o;
        return true;
    }

    // ------------------------------------------------------------ DoT (05 38)

    /// <summary><c>alvo varint, efeito u8, autor varint, varint, skill u32 (×100), valor varint</c></summary>
    private void ParseDot(ReadOnlySpan<byte> b)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var target) || !IsEntityId(target)) return;
        if (!Wire.TryU8(b, ref o, out var effect) || effect is not (0x02 or 0x0A)) return; // só dano
        if (!Wire.TryVarint(b, ref o, out var actor) || !IsEntityId(actor) || actor == target) return;
        if (!Wire.TryVarint(b, ref o, out _)) return;
        if (!Wire.TryU32(b, ref o, out var rawSkill)) return;
        var skill = (int)(rawSkill / 100);
        if (skill is < 1 or > 299_999_999) return;
        if (!Wire.TryVarint(b, ref o, out var amount) || amount is 0 or > 99_999_999) return;
        if (DotSkills.Count > 0 && !DotSkills.Contains(skill)) return;
        sink(new DamageEvent(TimeMs, actor, target, NormalizeSkill(skill), amount, false, true, false, 1));
    }

    // ------------------------------------------------------------ identidades (33 36 / 45 36)

    /// <summary><c>id varint, mask1 u32, mask2 u8, [mask2 &amp; 1] len u8 + nome utf8, servidor u16, classe u32, …</c></summary>
    private bool ParseIdentity(ReadOnlySpan<byte> b, int o, bool isSelf)
    {
        if (!Wire.TryVarint(b, ref o, out var id) || !IsEntityId(id)) return false;
        o += 4;
        if (o + 2 > b.Length || (b[o] & 0x01) == 0) return false;
        var len = b[o + 1];
        o += 2;
        if (len is < 1 or > 36 || o + len > b.Length) return false;
        var field = b.Slice(o, len);
        o += len;

        if (field.Length > 1 && field[0] == (byte)'$') // personagem do tutorial sem nome
        {
            if (isSelf) sink(new SelfEvent(TimeMs, id, "", GameClass.Unknown));
            return true;
        }
        if (!Wire.TryName(field, out var name)) return false;

        var cls = GameClass.Unknown;
        var server = 0;
        if (o + 6 <= b.Length)
        {
            var s = Wire.U16(b, o);
            var c = ClassFromWire(Wire.U32(b, o + 2));
            if (s is >= 1000 and < 3000 && c != GameClass.Unknown)
            {
                cls = c;
                server = s;
            }
            else if (!isSelf && o + 4 <= b.Length) cls = ClassFromWire(Wire.U32(b, o));
        }

        sink(isSelf ? new SelfEvent(TimeMs, id, name, cls, server) : new PlayerEvent(TimeMs, id, name, cls));
        return true;
    }

    private void ScanForIdentities(ReadOnlySpan<byte> b)
    {
        for (var i = 0; i + 10 < b.Length; i++)
        {
            if (b[i + 1] != 0x36) continue;
            var op = b[i];
            if (op != 0x33 && op != 0x45 && op != 0x44) continue;
            ParseIdentity(b, i + 2, op == 0x33);
        }
    }

    private void ScanEmbeddedBundles(ReadOnlySpan<byte> b)
    {
        for (var i = 1; i + 8 < b.Length; i++)
        {
            if (b[i] != 0xFF || b[i + 1] != 0xFF) continue;
            for (var n = 3; n >= 1; n--)
            {
                var at = i - n;
                if (at < 0 || !Wire.TryVarint(b, at, out var len, out var vlen) || vlen != n) continue;
                var end = at + (long)len + vlen - 4;
                if (len < 12 || end > b.Length) continue;
                var raw = Wire.U32(b, i + 2);
                if (raw is 0 or > FrameDecoder.MaxBundle) continue;
                var target = ArrayPool<byte>.Shared.Rent((int)raw);
                try
                {
                    var decoded = LZ4Codec.Decode(b[(i + 6)..(int)end], target.AsSpan(0, (int)raw));
                    if (decoded <= 0) continue;
                    ScanForIdentities(target.AsSpan(0, decoded));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(target);
                }
                i = (int)end - 1;
                break;
            }
        }
    }

    // ------------------------------------------------------------ spawns (41 36): NPCs e invocações

    private void ParseSpawn(ReadOnlySpan<byte> b)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var rawId) || rawId == 0) return;
        var id = rawId > 1_000_000 ? (rawId & 0x3FFF) | 0x4000 : rawId;
        if (o + 2 > b.Length) return;

        var maskStart = o;
        var mask = o + 4 <= b.Length ? Wire.U32(b, o) : (uint)b[o] | (uint)b[o + 1] << 8;
        var kind = b[o];

        // Nome inline (numa invocação: o nome do dono)
        string? ownerName = null;
        var cursor = maskStart + 5;
        foreach (var sub in (ReadOnlySpan<int>)[4, 2])
        {
            var g = maskStart + sub;
            if (g + 2 >= b.Length || (b[g] & 0x01) == 0) continue;
            var len = b[g + 1];
            if (len is < 1 or > 36 || g + 2 + len > b.Length) continue;
            if (!Wire.TryName(b.Slice(g + 2, len), out var n)) continue;
            ownerName = n;
            cursor = g + 2 + len;
            break;
        }

        var (npcCode, maxHp) = FindNpcCode(b, maskStart);
        var summonish = kind is 0x5F or 0x1C or 0x1F or 0x1D or 0x5D;

        if (kind == 0x5F && (mask & 0x10) != 0 && FindParentKey(b, cursor, id) is { } owner)
        {
            sink(new SummonEvent(TimeMs, id, owner, null));
            return;
        }
        if (summonish && ownerName is not null)
        {
            sink(new SummonEvent(TimeMs, id, 0, ownerName));
            return;
        }
        if (kind is 0x5F or 0x1F or 0x1D or 0x5D)
        {
            var so = FindSpiritOwner(b, maskStart);
            sink(new SummonEvent(TimeMs, id, so is { } s && s != id ? s : 0, null));
            return;
        }
        if (kind is 0x0C or 0x0D || (!summonish && npcCode != 0))
            sink(new NpcEvent(TimeMs, id, npcCode, maxHp));
    }

    /// <summary>Template do NPC: u24 logo antes do marcador <c>00 (00|40) 02</c>; HP vem depois como <c>01 cur max</c>.</summary>
    private static (int Code, long MaxHp) FindNpcCode(ReadOnlySpan<byte> b, int start)
    {
        var end = Math.Min(b.Length - 2, start + 60);
        for (var i = start; i < end; i++)
        {
            if (b[i] != 0x00 || (b[i + 1] != 0x40 && b[i + 1] != 0x00) || b[i + 2] != 0x02) continue;
            if (i < start + 3) return (0, 0);
            var code = (int)Wire.U24(b, i - 3);
            long maxHp = 0;
            var hpEnd = Math.Min(b.Length - 2, i + 3 + 64);
            for (var h = i + 3; h < hpEnd; h++)
            {
                if (b[h] != 0x01) continue;
                if (!Wire.TryVarint(b, h + 1, out var cur, out var l1) || cur == 0) continue;
                if (!Wire.TryVarint(b, h + 1 + l1, out var max, out _) || max < cur) continue;
                maxHp = max;
                break;
            }
            return (code, maxHp);
        }
        return (0, 0);
    }

    /// <summary>Bloco do dono: <c>06 | pai u32 | legião u32 | 00 00 | servidor u16 | len u8 | nome da legião</c>.</summary>
    private static uint? FindParentKey(ReadOnlySpan<byte> b, int from, uint selfId)
    {
        for (var i = Math.Max(1, from); i + 13 <= b.Length; i++)
        {
            if (b[i - 1] != 0x06) continue;
            var parent = Wire.U32(b, i);
            if (!IsEntityId(parent) || parent == selfId) continue;
            if (Wire.U16(b, i + 8) != 0) continue;
            var server = Wire.U16(b, i + 10);
            if (server is < 1000 or >= 3000) continue;
            var len = b[i + 12];
            if (len is < 1 or > 48 || i + 13 + len > b.Length) continue;
            if (!Wire.IsText(b.Slice(i + 13, len))) continue;
            return parent;
        }
        return null;
    }

    private static readonly byte[] SpiritAnchor = [0x80, 0x75, 0xD5, 0x2A, 0xBB, 0x03, 0x00, 0x00];

    private static uint? FindSpiritOwner(ReadOnlySpan<byte> b, int from)
    {
        var window = b[from..Math.Min(b.Length, from + 128 + SpiritAnchor.Length)];
        var i = window.IndexOf(SpiritAnchor);
        if (i < 0) return null;
        return Wire.TryVarint(b, from + i + SpiritAnchor.Length, out var owner, out _) && IsEntityId(owner) ? owner : null;
    }

    // ------------------------------------------------------------ HP / mapa

    /// <summary>
    /// <c>entidade varint, formato u8, [bit0: n u8, n × (tipo u8, u32)], [bit1: n u8, n × (tipo u8, i64)]</c>.
    /// O grupo de 4 bytes (bit 0) só vem para o seu personagem. No grupo de 8 bytes, tipo 0 = HP atual, 7 = HP máx.
    /// </summary>
    private void ParseEntityStats(ReadOnlySpan<byte> b)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var id) || !IsEntityId(id)) return;
        if (!Wire.TryU8(b, ref o, out var format) || (format & ~0x03) != 0) return;
        if ((format & 0x01) != 0)
        {
            if (!Wire.TryU8(b, ref o, out var n) || o + n * 5 > b.Length) return;
            o += n * 5;
        }
        long? hp = null, max = null;
        if ((format & 0x02) != 0)
        {
            if (!Wire.TryU8(b, ref o, out var n) || o + n * 9 > b.Length) return;
            for (var i = 0; i < n; i++, o += 9)
            {
                var v = BitConverter.ToInt64(b.Slice(o + 1, 8));
                if (b[o] == 0) hp = v;
                else if (b[o] == 7) max = v;
            }
        }
        if (o != b.Length) return;
        if ((format & 0x01) != 0) sink(new SelfStatsEvent(TimeMs, id));
        else if (hp is { } h && h is >= 0 and < 100_000_000_000) sink(new HpEvent(TimeMs, id, h, max ?? 0));
        else if (max is { } m && m > 0) sink(new HpEvent(TimeMs, id, -1, m));
    }

    /// <summary>
    /// <c>02 97</c> seu grupo: <c>chave u32, título str, u8, dungeon u32, 13 bytes, n varint</c>, depois os membros,
    /// achados pelo formato do cabeçalho: <c>presença u8 ≠ 0, slot u8 crescente, dbid u64 (16 bits altos = servidor),
    /// nome str, classe u32, nível u32</c>.
    /// </summary>
    /// <summary><c>u16, entidade varint, u32, idPersonagem u32</c></summary>
    private void ParseCharacterLink(ReadOnlySpan<byte> b)
    {
        var o = 2;
        if (!Wire.TryVarint(b, ref o, out var id) || !IsEntityId(id)) return;
        o += 4;
        if (!Wire.TryU32(b, ref o, out var charId) || charId == 0) return;
        sink(new CharacterLinkEvent(TimeMs, id, charId));
    }

    private void ParsePartyRoster(ReadOnlySpan<byte> b)
    {
        var o = 4;
        if (o >= b.Length) return;
        o += 1 + b[o]; // título
        o += 1 + 4 + 2 + 8 + 3;
        if (o > b.Length || !Wire.TryVarint(b, ref o, out var count)) return;
        var max = (int)Math.Min(count == 0 ? 8u : count, 24u);

        var members = new List<PartyMember>();
        var prevSlot = 0;
        for (var i = o; i + 19 <= b.Length && members.Count < max; i++)
        {
            var presence = b[i];
            var slot = b[i + 1];
            if (presence == 0 || slot <= prevSlot || slot > 24) continue;
            var dbid = BitConverter.ToUInt64(b.Slice(i + 2, 8));
            var server = (ushort)(dbid >> 48);
            if (server is < 1000 or > 2999 || (uint)dbid == 0 || ((dbid >> 32) & 0xFFFF) != 0) continue;
            var len = b[i + 10];
            if (len is < 1 or > 48 || i + 11 + len + 8 > b.Length) continue;
            if (!Wire.TryName(b.Slice(i + 11, len), out var name)) continue;
            var p = i + 11 + len;
            var cls = Wire.U32(b, p);
            var lvl = Wire.U32(b, p + 4);
            if (cls is < 1 or > 63 || lvl is < 1 or > 99) continue;

            // Depois do nível: [presença & 1: u32 nível de conquistador], gear score u32,
            // e o CP como u64 cinco bytes depois de um u16 de servidor (0..3 bytes adiante).
            var q = p + 8;
            if ((presence & 0x01) != 0) q += 4;
            uint? gear = null;
            ulong? cp = null;
            if (q + 4 <= b.Length && Wire.U32(b, q) is var gs and < 100_000)
            {
                gear = gs;
                q += 4;
            }
            for (var k = 0; k <= 3 && q + k + 13 <= b.Length; k++)
            {
                if (Wire.U16(b, q + k) is < 1000 or > 2999) continue;
                var v = BitConverter.ToUInt64(b.Slice(q + k + 5, 8));
                if (v < 10_000_000_000UL) cp = v;
                break;
            }

            members.Add(new PartyMember(name, ClassFromWire(cls), (int)lvl, gear, cp, (uint)dbid, server));
            prevSlot = slot;
            i = p + 7;
        }
        if (members.Count > 0) sink(new PartyEvent(TimeMs, members));
    }

    /// <summary><c>entidade varint, hp varint, hpMax varint</c></summary>
    private void ParseHpMp(ReadOnlySpan<byte> b)
    {
        var o = 0;
        if (!Wire.TryVarint(b, ref o, out var id) || !IsEntityId(id)) return;
        if (!Wire.TryVarint(b, ref o, out var hp) || !Wire.TryVarint(b, ref o, out var max)) return;
        if (max == 0 || hp > max) return;
        sink(new HpEvent(TimeMs, id, hp, max));
    }

    private void ParseMapLoad(ReadOnlySpan<byte> b)
    {
        var o = 0;
        if (!Wire.TryU32(b, ref o, out _) || !Wire.TryU32(b, ref o, out var mapId)) return;
        if (mapId is 0 or > 9_999_999) return;
        sink(new ZoneEvent(TimeMs, (int)mapId));
    }

    // ------------------------------------------------------------ helpers

    private static bool IsEntityId(uint id) => id is >= 1 and <= 9_999_999;

    public static bool IsClassSkill(int code) => code is >= 10_000_000 and <= 19_999_999;

    public static int BaseSkill(int code) => IsClassSkill(code) ? code - code % 10_000 : code;

    /// <summary>Colapsa dígitos de nível/especialização de skills de classe.</summary>
    private static int NormalizeSkill(int raw) => BaseSkill(raw);

    public static GameClass ClassFromSkill(int code)
    {
        if (!IsClassSkill(code) || code / 10_000 % 100 == 0) return GameClass.Unknown;
        return (code / 1_000_000) switch
        {
            11 => GameClass.Gladiator,
            12 => GameClass.Templar,
            13 => GameClass.Assassin,
            14 => GameClass.Ranger,
            15 => GameClass.Sorcerer,
            16 => GameClass.Elementalist,
            17 => GameClass.Cleric,
            18 => GameClass.Chanter,
            19 => GameClass.Brawler,
            _ => GameClass.Unknown,
        };
    }

    public static GameClass ClassFromWire(uint id) => id switch
    {
        >= 5 and <= 8 => GameClass.Gladiator,
        >= 9 and <= 12 => GameClass.Templar,
        >= 13 and <= 16 => GameClass.Ranger,
        >= 17 and <= 20 => GameClass.Assassin,
        >= 21 and <= 24 => GameClass.Elementalist,
        >= 25 and <= 28 => GameClass.Sorcerer,
        >= 29 and <= 32 => GameClass.Cleric,
        >= 33 and <= 36 => GameClass.Chanter,
        >= 45 and <= 48 => GameClass.Brawler,
        _ => GameClass.Unknown,
    };
}
