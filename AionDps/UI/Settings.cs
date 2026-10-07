using System.Text.Json;

namespace AionDps.UI;

/// <summary>Preferências salvas em settings.json ao lado do .exe.</summary>
public sealed class Settings
{
    public Keys ToggleKey { get; set; } = Keys.Control | Keys.Shift | Keys.H;
    public Keys ResetKey { get; set; } = Keys.Control | Keys.Shift | Keys.R;

    /// <summary>Tempo-alvo para matar um boss: base da bolinha de ritmo.</summary>
    public int TargetSeconds { get; set; } = 300;

    /// <summary>Diferença (pontos percentuais) entre vida perdida e tempo passado que ainda conta como "no ritmo".</summary>
    public double PaceTolerance { get; set; } = 1.0;

    /// <summary>Nome do seu personagem (reserva, enquanto o jogo não manda o nome; também usado para buscar GS/CP).</summary>
    public string CharacterName { get; set; } = "";

    /// <summary>Servidor do personagem (aprendido do jogo; 0 = desconhecido).</summary>
    public int CharacterServer { get; set; }

    public bool BossOnly { get; set; } = true;
    public bool PartyOnly { get; set; } = true;
    public int OpacityPct { get; set; } = 92;

    public int X { get; set; } = 40;
    public int Y { get; set; } = 200;
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 300;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* arquivo corrompido: volta ao padrão */ }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }

    public static string FormatTime(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>"5:00", "300" ou "5m" → segundos.</summary>
    public static bool TryParseTime(string text, out int seconds)
    {
        seconds = 0;
        text = text.Trim().ToLowerInvariant().TrimEnd('s');
        if (text.EndsWith('m') && int.TryParse(text[..^1], out var m)) seconds = m * 60;
        else if (text.Split(':') is [var a, var b] && int.TryParse(a, out var mm) && int.TryParse(b, out var ss) && ss < 60) seconds = mm * 60 + ss;
        else if (int.TryParse(text, out var s)) seconds = s;
        return seconds is > 0 and <= 24 * 3600;
    }

    /// <summary>"Ctrl+Shift+H"; sem tecla = "(nenhum)".</summary>
    public static string Describe(Keys k)
    {
        if ((k & Keys.KeyCode) == Keys.None) return "(nenhum)";
        var parts = Modifiers(k);
        var code = k & Keys.KeyCode;
        parts.Add(code switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            >= Keys.NumPad0 and <= Keys.NumPad9 => "Num" + (code - Keys.NumPad0),
            Keys.Oemtilde => "'",
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            _ => code.ToString(),
        });
        return string.Join("+", parts);
    }

    public static List<string> Modifiers(Keys k)
    {
        var parts = new List<string>();
        if (k.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (k.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (k.HasFlag(Keys.Shift)) parts.Add("Shift");
        return parts;
    }
}
