using AionDps.Protocol;

namespace AionDps.Meter;

public sealed record SkillRow(int Skill, string Name, long Damage, int Hits, double CritRate, long MaxHit, double Share);

public sealed record PlayerRow(uint Id, string Name, GameClass Class, bool IsSelf, long Damage, double Dps, double Share,
    int Hits, double CritRate, long MaxHit, List<SkillRow> Skills, uint? GearScore, ulong? CombatPower, bool GearIsItemLevel = false);

/// <param name="View">"Total" (todas as lutas desde o último clean) ou "Luta i/N".</param>
/// <param name="Seconds">Tempo em combate usado no DPS (no Total, a soma das lutas).</param>
/// <param name="FightSeconds">Duração da luta do cabeçalho (no Total, a mais recente).</param>
/// <param name="BossHpLostPct">Pontos percentuais de vida que o boss perdeu desde o início da luta (null = sem boss/HP).</param>
public sealed record Snapshot(string View, int Fights, bool Active, double Seconds, double FightSeconds, long Total,
    string? Target, double? TargetHpPct, List<PlayerRow> Rows, double? BossHpLostPct);

/// <summary>
/// Junta o dano em lutas.
/// <list type="bullet">
/// <item><see cref="BossOnly"/> (padrão): a luta começa quando você ou alguém do grupo acerta um boss, e termina quando
/// o boss morre ou depois de <see cref="BossIdleMs"/> sem dano nele.</item>
/// <item>Senão: começa com qualquer dano seu e termina após <see cref="IdleMs"/> parado.</item>
/// </list>
/// <see cref="PartyOnly"/> (padrão) mostra só você + membros do grupo (lista <c>02 97</c>). Sem grupo detectado, mostra
/// todos os jogadores que bateram no boss.
/// </summary>
public sealed class CombatMeter
{
    public const long IdleMs = 15_000;
    public const long BossIdleMs = 30_000;
    private const int MaxHistory = 20;

    private readonly GameData _data;
    private readonly object _gate = new();

    private uint _selfId;
    private bool _selfAuthoritative;
    private string _selfName = "";
    private int _selfServer;
    private readonly Dictionary<uint, uint> _charLinks = new(); // id do personagem → entidade
    private readonly Dictionary<uint, HashSet<int>> _scalars = new(); // escalares de poder vistos por jogador
    private string _fallbackName = "";
    private bool _selfNameConfirmed;
    private readonly Dictionary<string, ArmoryProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, int> _selfVotes = new();
    private readonly Dictionary<uint, (string Name, GameClass Class)> _players = new();
    private readonly Dictionary<uint, uint> _summonOwner = new();
    private readonly Dictionary<uint, string> _summonOwnerName = new();
    private readonly Dictionary<uint, int> _npcCode = new();
    private readonly Dictionary<uint, (long Hp, long Max)> _hp = new();
    private List<PartyMember> _party = new();
    private readonly List<Fight> _fights = new();

    public CombatMeter(GameData data) => _data = data;

    public bool BossOnly { get; set; } = true;
    public bool PartyOnly { get; set; } = true;
    public string SelfName { get { lock (_gate) return _selfName; } }
    public int PartySize { get { lock (_gate) return _party.Count; } }
    public long EventCount { get; private set; }
    public int ViewIndex { get; set; } = -1;

    public void OnEvent(GameEvent e)
    {
        lock (_gate)
        {
            EventCount++;
            switch (e)
            {
                case SelfEvent s:
                    _selfId = s.Id;
                    _selfAuthoritative = true;
                    if (s.Name.Length > 0)
                    {
                        _selfName = s.Name;
                        _selfNameConfirmed = true;
                    }
                    if (s.Server > 0) _selfServer = s.Server;
                    _players[s.Id] = (_selfName.Length > 0 ? _selfName : "Você", s.Class);
                    break;
                case SelfStatsEvent s:
                    NoteSelfStats(s.Id);
                    break;
                case PlayerEvent p:
                    if (p.Id == _selfId) break;
                    _players[p.Id] = (p.Name, p.Class != GameClass.Unknown ? p.Class
                        : _players.TryGetValue(p.Id, out var old) ? old.Class : GameClass.Unknown);
                    _npcCode.Remove(p.Id);
                    JoinRoster();
                    break;
                case PartyEvent pe:
                    _party = pe.Members;
                    JoinRoster();
                    break;
                case CharacterLinkEvent l:
                    _charLinks[l.CharacterId] = l.Id;
                    JoinRoster();
                    break;
                case SummonEvent s:
                    if (s.OwnerId != 0) _summonOwner[s.Id] = s.OwnerId;
                    else if (s.OwnerName is not null) _summonOwnerName[s.Id] = s.OwnerName;
                    else _summonOwner.TryAdd(s.Id, 0); // efeito/espírito sem dono conhecido: nunca é jogador
                    break;
                case NpcEvent n:
                    _npcCode[n.Id] = n.Code;
                    if (n.MaxHp > 0) _hp[n.Id] = (n.MaxHp, n.MaxHp);
                    _summonOwner.Remove(n.Id);
                    _players.Remove(n.Id);
                    break;
                case HpEvent h:
                    OnHp(h);
                    break;
                case ZoneEvent:
                    _summonOwner.Clear();
                    _summonOwnerName.Clear();
                    _npcCode.Clear();
                    _hp.Clear();
                    break;
                case DamageEvent d:
                    OnDamage(d);
                    break;
            }
        }
    }

    // ------------------------------------------------------------ identidade

    /// <summary>Status "só do próprio personagem": 5 votos e na liderança = é você.</summary>
    private void NoteSelfStats(uint id)
    {
        if (_selfAuthoritative || id == _selfId) return;
        var v = _selfVotes[id] = _selfVotes.GetValueOrDefault(id) + 1;
        if (v < 5 || _selfVotes.Any(kv => kv.Key != id && kv.Value >= v)) return;
        _selfId = id;
        var cls = _players.TryGetValue(id, out var p) ? p.Class : GameClass.Unknown;
        _players[id] = (_selfName.Length > 0 ? _selfName : "Você", cls);
        JoinRoster();
        ApplyFallbackName();
    }

    /// <summary>
    /// Liga cada membro da lista do grupo a uma entidade: pelo nome, pelo id do personagem (<c>20 36</c>) e, para quem
    /// sobrar, pela classe quando ela é única (ex.: o único Elementalista da lista = a única entidade Elementalista sem nome).
    /// </summary>
    private void JoinRoster()
    {
        if (_party.Count == 0) return;
        var names = _players.Where(kv => !IsPlaceholder(kv.Value.Name)).Select(kv => kv.Value.Name).ToHashSet();
        var unbound = _party.Where(m => !names.Contains(m.Name)).ToList();

        foreach (var m in unbound.ToList())
        {
            if (m.CharacterId == 0 || !_charLinks.TryGetValue(m.CharacterId, out var id)) continue;
            if (_players.TryGetValue(id, out var p) && !IsPlaceholder(p.Name) && !(id == _selfId && !_selfAuthoritative)) continue;
            Bind(id, m);
            unbound.Remove(m);
        }

        foreach (var group in unbound.GroupBy(m => m.Class).Where(g => g.Key != GameClass.Unknown && g.Count() == 1).ToList())
        {
            var candidates = _players.Where(kv => kv.Value.Class == group.Key && IsPlaceholder(kv.Value.Name)).Select(kv => kv.Key).ToList();
            // você conta como "sem nome" enquanto o nome não for confirmado pelo jogo
            if (_selfId != 0 && !_selfAuthoritative && !candidates.Contains(_selfId) && _players.TryGetValue(_selfId, out var me)
                && me.Class == group.Key && !_party.Any(x => x.Name == _selfName))
                candidates.Add(_selfId);
            if (candidates.Count == 1) Bind(candidates[0], group.First());
        }
    }

    private void Bind(uint id, PartyMember m)
    {
        var cls = _players.TryGetValue(id, out var p) && p.Class != GameClass.Unknown ? p.Class : m.Class;
        _players[id] = (m.Name, cls);
        if (id != _selfId) return;
        _selfName = m.Name;
        _selfNameConfirmed = true;
        if (m.Server > 0) _selfServer = m.Server;
    }

    /// <summary>Nome provisório: "#123" (jogador sem nome) ou "Você".</summary>
    private static bool IsPlaceholder(string name) => name.Length == 0 || name.StartsWith("#") || name == "Você";

    /// <summary>Nome configurado pelo usuário: usado enquanto o jogo não mandou o nome do personagem.</summary>
    public string FallbackSelfName
    {
        set
        {
            lock (_gate)
            {
                _fallbackName = value.Trim();
                ApplyFallbackName();
            }
        }
    }

    private void ApplyFallbackName()
    {
        if (_selfId == 0 || _selfAuthoritative || _fallbackName.Length == 0) return;
        if (_selfName.Length > 0 && _party.Any(m => m.Name == _selfName)) return; // nome confirmado pelo grupo
        _selfName = _fallbackName;
        var cls = _players.TryGetValue(_selfId, out var p) ? p.Class : GameClass.Unknown;
        _players[_selfId] = (_selfName, cls);
    }

    /// <summary>Servidor salvo junto com o nome configurado (ajuda a busca no site oficial).</summary>
    public int FallbackSelfServer
    {
        set
        {
            lock (_gate)
                if (_selfServer == 0) _selfServer = value;
        }
    }

    /// <summary>Nome e servidor confirmados pelo jogo (pacote do personagem ou lista do grupo), para salvar.</summary>
    public (string Name, int Server)? ConfirmedSelf
    {
        get
        {
            lock (_gate) return _selfNameConfirmed && _selfName.Length > 0 ? (_selfName, _selfServer) : null;
        }
    }

    /// <summary>Estado de identidade para o log de diagnóstico.</summary>
    public string DebugState()
    {
        lock (_gate)
        {
            var party = string.Join(", ", _party.Select(m => $"{m.Name}/{m.Class}/cid={m.CharacterId}/srv={m.Server}/gs={m.GearScore}/cp={m.CombatPower}"));
            var links = string.Join(", ", _charLinks.Select(kv => $"{kv.Key}->{kv.Value}"));
            var players = string.Join(", ", _players.Select(kv => $"{kv.Key}={kv.Value.Name}/{kv.Value.Class}"));
            var boss = _fights.Count > 0 && _fights[^1].BossId is var b and not 0
                ? $"boss={b} code={_npcCode.GetValueOrDefault(b)} hp={_hp.GetValueOrDefault(b)} start={_fights[^1].StartHpPct}"
                : "boss=-";
            return $"self={_selfId} auth={_selfAuthoritative} nome='{_selfName}' srv={_selfServer}\n  party: {party}\n  links: {links}\n  players: {players}\n  {boss}";
        }
    }

    public void SetProfile(string name, ArmoryProfile profile)
    {
        lock (_gate) _profiles[name] = profile;
    }

    /// <summary>Jogadores com nome que aparecem nas lutas e ainda não têm GS/CP (nem pela lista do grupo).</summary>
    public List<(string Name, int Server, GameClass Class)> PlayersNeedingProfile()
    {
        lock (_gate)
        {
            var ids = new HashSet<uint>();
            if (_selfId != 0) ids.Add(_selfId);
            foreach (var f in _fights.TakeLast(2))
                foreach (var h in f.Hits)
                    ids.Add(h.Actor);
            var result = new List<(string, int, GameClass)>();
            foreach (var id in ids)
            {
                if (!_players.TryGetValue(id, out var p) || p.Name.Length == 0 || p.Name.StartsWith("#") || p.Name == "Você") continue;
                if (_party.Any(m => m.Name == p.Name && m.CombatPower is not null)) continue;
                result.Add((p.Name, id == _selfId ? _selfServer : 0, p.Class));
            }
            return result;
        }
    }

    private bool IsPlayer(uint id) => id == _selfId || _players.ContainsKey(id);

    private bool IsPartyMember(uint id)
    {
        if (id == _selfId) return true;
        if (_party.Count == 0) return _selfId == 0; // sem grupo: só você (todos, enquanto você não foi identificado)
        return _players.TryGetValue(id, out var p) && _party.Any(m => m.Name == p.Name);
    }

    private double? HpPct(uint id) => _hp.TryGetValue(id, out var hp) && hp.Max > 0 && hp.Hp >= 0 ? 100.0 * hp.Hp / hp.Max : null;

    private bool IsBoss(uint target) =>
        _npcCode.TryGetValue(target, out var code) && _data.Npcs.TryGetValue(code, out var npc) && npc.IsBoss;

    // ------------------------------------------------------------ combate

    private void OnHp(HpEvent h)
    {
        var prev = _hp.GetValueOrDefault(h.Id);
        var max = h.MaxHp > 0 ? h.MaxHp : prev.Max;
        var hp = h.Hp >= 0 ? h.Hp : prev.Hp > 0 ? prev.Hp : max;
        _hp[h.Id] = (hp, Math.Max(max, hp));

        // Boss morreu: encerra a luta.
        if (h.Hp == 0 && _fights.Count > 0 && _fights[^1] is { Ended: false } f && f.BossId == h.Id)
        {
            f.Ended = true;
            f.LastMs = Math.Max(f.LastMs, h.TimeMs);
        }
    }

    private void OnDamage(DamageEvent d)
    {
        if (_data.IsNonDamageSkill(d.Skill)) return;
        if (IsPlayer(d.Target)) return; // ignora dano em jogadores

        var actor = ResolveOwner(d.Actor);
        var unownedSummon = actor == 0; // espírito/efeito sem dono conhecido
        if (unownedSummon) actor = d.Actor;

        if (IsKnownPlayer(actor)) RecordScalar(actor, d.Scalar);
        else if (AdoptOrphan(actor, d) is { } owner) actor = owner; // invocação: o dano vai para o dono
        else if (unownedSummon || PacketParser.ClassFromSkill(d.Skill) == GameClass.Unknown)
        {
            // Dono ainda desconhecido: guarda o golpe com o id da invocação (não aparece) até achar o dono.
            if (!_npcCode.ContainsKey(actor)) _fights.LastOrDefault()?.Hits.Add(new Hit(d.TimeMs, actor, d.Target, d.Skill, d.Amount, d.Crit));
            return;
        }

        if (!IsPlayer(actor))
        {
            // Jogador ainda sem nome, mas usando skill de classe: registra como jogador.
            var cls = PacketParser.ClassFromSkill(d.Skill);
            if (cls == GameClass.Unknown || _npcCode.ContainsKey(actor) || _summonOwner.ContainsKey(actor)) return;
            _players[actor] = ($"#{actor}", cls);
            JoinRoster();
        }
        if (_players.TryGetValue(actor, out var info) && info.Class == GameClass.Unknown)
        {
            var cls = PacketParser.ClassFromSkill(d.Skill);
            if (cls != GameClass.Unknown)
            {
                _players[actor] = (info.Name, cls);
                JoinRoster();
            }
        }

        var boss = IsBoss(d.Target);
        var current = _fights.Count > 0 ? _fights[^1] : null;
        if (current is not null && (current.Ended || d.TimeMs - current.LastMs > (current.BossId != 0 ? BossIdleMs : IdleMs)))
            current = null;

        if (current is null)
        {
            bool starts = BossOnly
                ? boss && IsPartyMember(actor)
                : actor == _selfId || _selfId == 0;
            if (!starts) return;
            current = new Fight { StartMs = d.TimeMs, BossId = boss ? d.Target : 0, StartHpPct = boss ? HpPct(d.Target) : null };
            _fights.Add(current);
            if (_fights.Count > MaxHistory) _fights.RemoveAt(0);
            ViewIndex = -1;
        }

        if (actor == _selfId) current.SelfTargets.Add(d.Target);
        current.Hits.Add(new Hit(d.TimeMs, actor, d.Target, d.Skill, d.Amount, d.Crit));

        var keepsAlive = current.BossId != 0
            ? d.Target == current.BossId || (boss && IsPartyMember(actor))
            : actor == _selfId || current.SelfTargets.Contains(d.Target) || _selfId == 0;
        if (keepsAlive) current.LastMs = Math.Max(current.LastMs, d.TimeMs);
        if (boss && current.BossId == 0)
        {
            current.BossId = d.Target;
            current.StartHpPct = HpPct(d.Target);
        }
    }

    /// <summary>Você ou um jogador com nome real (não "#id").</summary>
    private bool IsKnownPlayer(uint id) =>
        id == _selfId || (_players.TryGetValue(id, out var p) && !IsPlaceholder(p.Name));

    private void RecordScalar(uint id, int scalar)
    {
        if (scalar is < 1_000 or > 200_000) return;
        if (!_scalars.TryGetValue(id, out var set)) _scalars[id] = set = new HashSet<int>();
        if (set.Count < 64) set.Add(scalar);
    }

    /// <summary>
    /// Invocações (espíritos do Elementalista etc.) carregam o escalar de poder do dono em cada golpe. Se exatamente um
    /// jogador conhecido (da mesma classe, quando a skill diz a classe) tem esse escalar (ou +1000, buff), ele é o dono:
    /// a invocação passa a contar para ele, inclusive os golpes já registrados.
    /// </summary>
    private uint? AdoptOrphan(uint orphan, DamageEvent d)
    {
        if (d.Scalar is < 1_000 or > 200_000) return null;
        var cls = PacketParser.ClassFromSkill(d.Skill);
        uint? owner = null;
        var count = 0;
        foreach (var (id, set) in _scalars)
        {
            if (id == orphan || !IsKnownPlayer(id)) continue;
            if (cls != GameClass.Unknown && _players.TryGetValue(id, out var p) && p.Class != GameClass.Unknown && p.Class != cls) continue;
            if (!set.Contains(d.Scalar) && !set.Contains(d.Scalar + 1_000)) continue;
            owner = id;
            count++;
        }
        if (count != 1 || owner is not { } o) return null;

        _summonOwner[orphan] = o;
        if (d.Actor != orphan) _summonOwner[d.Actor] = o;
        if (_players.TryGetValue(orphan, out var placeholder) && IsPlaceholder(placeholder.Name)) _players.Remove(orphan);
        foreach (var f in _fights)
        {
            for (var i = 0; i < f.Hits.Count; i++)
                if (f.Hits[i].Actor == orphan)
                {
                    f.Hits[i] = f.Hits[i] with { Actor = o };
                    if (o == _selfId) f.SelfTargets.Add(f.Hits[i].Target);
                }
        }
        return o;
    }

    private uint ResolveOwner(uint id)
    {
        for (var depth = 0; depth < 4; depth++)
        {
            if (_summonOwner.TryGetValue(id, out var owner))
            {
                if (owner == 0) return 0;
                id = owner;
                continue;
            }
            if (_summonOwnerName.TryGetValue(id, out var name))
            {
                if (name == _selfName && _selfId != 0) return _selfId;
                foreach (var (pid, p) in _players)
                    if (p.Name == name) return pid;
                return 0;
            }
            break;
        }
        return id;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _fights.Clear();
            ViewIndex = -1;
        }
    }

    public void Step(int delta)
    {
        lock (_gate)
        {
            // Ordem: luta 1 … luta N, Total (o Total fica depois da mais recente).
            if (_fights.Count == 0) return;
            var i = ViewIndex < 0 ? _fights.Count : ViewIndex;
            i = Math.Clamp(i + delta, 0, _fights.Count);
            ViewIndex = i == _fights.Count ? -1 : i;
        }
    }

    public Snapshot? Snapshot(long nowMs)
    {
        lock (_gate)
        {
            if (_fights.Count == 0) return null;
            var isTotal = ViewIndex < 0 || ViewIndex >= _fights.Count;
            var f = isTotal ? _fights[^1] : _fights[ViewIndex]; // luta do cabeçalho (boss, HP, ritmo)
            var fights = isTotal ? _fights : [f];
            var idle = f.BossId != 0 ? BossIdleMs : IdleMs;
            var active = f == _fights[^1] && !f.Ended && nowMs - f.LastMs <= idle;
            var fightSeconds = Math.Max(1.0, (f.LastMs - f.StartMs) / 1000.0);
            var seconds = Math.Max(1.0, fights.Sum(x => (x.LastMs - x.StartMs) / 1000.0));

            var per = new Dictionary<uint, Acc>();
            var perTarget = new Dictionary<uint, long>();
            foreach (var fight in fights)
            {
                foreach (var h in fight.Hits)
                {
                    if (h.TimeMs > fight.LastMs) continue;
                    if (!IsPlayer(h.Actor)) continue; // invocação cujo dono ainda não foi achado
                    if (PartyOnly && !IsPartyMember(h.Actor)) continue;
                    if (fight.BossId == 0 && _selfId != 0 && h.Actor != _selfId && !fight.SelfTargets.Contains(h.Target)) continue;
                    if (!per.TryGetValue(h.Actor, out var a)) per[h.Actor] = a = new Acc();
                    a.Add(h);
                    if (fight == f) perTarget[h.Target] = perTarget.GetValueOrDefault(h.Target) + h.Amount;
                }
            }
            var total = per.Values.Sum(a => a.Damage);

            var rows = per
                .OrderByDescending(kv => kv.Value.Damage)
                .Select(kv =>
                {
                    var (name, cls) = _players.TryGetValue(kv.Key, out var p) ? p : ($"#{kv.Key}", GameClass.Unknown);
                    var member = _party.FirstOrDefault(m => m.Name == name);
                    _profiles.TryGetValue(name, out var web);
                    if (cls == GameClass.Unknown && member is not null) cls = member.Class;
                    var a = kv.Value;
                    var skills = a.Skills
                        .OrderByDescending(s => s.Value.Damage)
                        .Select(s => new SkillRow(s.Key, _data.SkillName(s.Key), s.Value.Damage, s.Value.Count,
                            s.Value.Count == 0 ? 0 : (double)s.Value.Crits / s.Value.Count, s.Value.MaxHit,
                            a.Damage == 0 ? 0 : (double)s.Value.Damage / a.Damage))
                        .ToList();
                    return new PlayerRow(kv.Key, name, cls, kv.Key == _selfId, a.Damage, a.Damage / seconds,
                        total == 0 ? 0 : (double)a.Damage / total, a.Count, a.Count == 0 ? 0 : (double)a.Crits / a.Count,
                        a.MaxHit, skills, member?.GearScore ?? web?.ItemLevel, member?.CombatPower ?? web?.CombatPower,
                        GearIsItemLevel: member?.GearScore is null && web?.ItemLevel is not null);
                })
                .ToList();

            // Alvo mostrado: o boss da luta, ou quem mais apanhou.
            uint top = f.BossId != 0 ? f.BossId : perTarget.Count > 0 ? perTarget.MaxBy(kv => kv.Value).Key : 0;
            string? target = null;
            double? hpPct = null;
            if (top != 0)
            {
                target = _npcCode.TryGetValue(top, out var code) && _data.Npcs.TryGetValue(code, out var npc) && npc.Name.Length > 0
                    ? npc.Name
                    : $"Alvo #{top}";
                if (_hp.TryGetValue(top, out var hp) && hp.Max > 0) hpPct = 100.0 * hp.Hp / hp.Max;
            }

            double? lost = null;
            if (f.BossId != 0 && HpPct(f.BossId) is { } now) lost = Math.Max(0, (f.StartHpPct ?? 100) - now);

            var view = isTotal ? "Total" : $"Luta {ViewIndex + 1}/{_fights.Count}";
            return new Snapshot(view, _fights.Count, active, seconds, fightSeconds, total, target, hpPct, rows, lost);
        }
    }

    private readonly record struct Hit(long TimeMs, uint Actor, uint Target, int Skill, long Amount, bool Crit);

    private sealed class Fight
    {
        public long StartMs;
        public long LastMs;
        public uint BossId;
        /// <summary>HP% do boss quando a luta começou (null = desconhecido; assume 100%).</summary>
        public double? StartHpPct;
        public bool Ended;
        public readonly List<Hit> Hits = new();
        public readonly HashSet<uint> SelfTargets = new();
    }

    private sealed class Acc
    {
        public long Damage, MaxHit;
        public int Count, Crits;
        public readonly Dictionary<int, Acc> Skills = new();

        public void Add(Hit h, bool nested = false)
        {
            Damage += h.Amount;
            Count++;
            if (h.Crit) Crits++;
            if (h.Amount > MaxHit) MaxHit = h.Amount;
            if (nested) return;
            if (!Skills.TryGetValue(h.Skill, out var s)) Skills[h.Skill] = s = new Acc();
            s.Add(h, nested: true);
        }
    }
}
