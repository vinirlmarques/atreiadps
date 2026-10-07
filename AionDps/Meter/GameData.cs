using System.Text.Json;
using AionDps.Protocol;

namespace AionDps.Meter;

/// <summary>Nomes de skills/NPCs e listas auxiliares, lidos da pasta data/ (se existir).</summary>
public sealed class GameData
{
    public Dictionary<int, string> Skills { get; } = new();
    public Dictionary<int, (string Name, bool IsBoss)> Npcs { get; } = new();
    public HashSet<int> DotSkills { get; } = new();
    public HashSet<int> HealSkills { get; } = new();

    public static GameData Load(string dir)
    {
        var d = new GameData();
        Try(() =>
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "skills.json")))!)
                if (int.TryParse(k, out var id)) d.Skills[id] = v;
        });
        Try(() =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "npcs.json")));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(p.Name, out var id)) continue;
                var name = p.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var boss = p.Value.TryGetProperty("isBoss", out var b) && b.GetBoolean();
                d.Npcs[id] = (name, boss);
            }
        });
        Try(() => d.DotSkills.UnionWith(JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(dir, "dot_skill_ids.json")))!));
        Try(() => d.HealSkills.UnionWith(JsonSerializer.Deserialize<int[]>(File.ReadAllText(Path.Combine(dir, "healing_skill_ids.json")))!));
        return d;
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* arquivo de dados ausente: segue sem nomes */ }
    }

    public string SkillName(int code)
    {
        if (Skills.TryGetValue(code, out var name)) return name;
        var b = PacketParser.BaseSkill(code);
        if (b != code && Skills.TryGetValue(b, out name)) return name;
        return $"Skill {code}";
    }

    /// <summary>Registros no opcode de dano que não são dano: links de espírito do Elementalista e curas.</summary>
    public bool IsNonDamageSkill(int code) =>
        code is >= 16_990_000 and <= 16_999_999 or >= 16_770_000 and <= 16_779_999
        || HealSkills.Contains(code) || HealSkills.Contains(PacketParser.BaseSkill(code));
}
