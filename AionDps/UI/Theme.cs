using System.Drawing.Drawing2D;
using AionDps.Protocol;

namespace AionDps.UI;

/// <summary>Paleta, fontes e helpers de desenho compartilhados pelo overlay e pela janela de configurações.</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(14, 16, 21);
    public static readonly Color Surface = Color.FromArgb(21, 24, 31);
    public static readonly Color SurfaceHi = Color.FromArgb(30, 34, 44);
    public static readonly Color Border = Color.FromArgb(44, 50, 64);
    public static readonly Color Text = Color.FromArgb(236, 238, 243);
    public static readonly Color TextDim = Color.FromArgb(150, 157, 172);
    public static readonly Color TextFaint = Color.FromArgb(98, 105, 120);
    public static readonly Color Accent = Color.FromArgb(232, 190, 104);   // dourado (boss, você)
    public static readonly Color Good = Color.FromArgb(74, 210, 128);
    public static readonly Color Warn = Color.FromArgb(240, 196, 72);
    public static readonly Color Bad = Color.FromArgb(236, 88, 88);
    public static readonly Color HpFill = Color.FromArgb(200, 64, 72);

    public static readonly Font Ui = new("Segoe UI", 9f);
    public static readonly Font UiBold = new("Segoe UI Semibold", 9f);
    public static readonly Font UiSmall = new("Segoe UI", 7.75f);
    public static readonly Font UiTiny = new("Segoe UI Semibold", 7f);
    public static readonly Font Num = Pick("Bahnschrift SemiBold", "Segoe UI Semibold", 10.5f);
    public static readonly Font NumBig = Pick("Bahnschrift SemiBold", "Segoe UI Semibold", 11.5f);
    public static readonly Font NumSmall = Pick("Bahnschrift", "Segoe UI", 8f);
    public static readonly Font Icons = new("Segoe MDL2 Assets", 9.5f);
    public static readonly Font IconsSmall = new("Segoe MDL2 Assets", 7.5f);

    /// <summary>Ícone do app (embutido no exe), no tamanho pedido.</summary>
    public static Icon AppIcon(int size)
    {
        using var s = typeof(Theme).Assembly.GetManifestResourceStream("AtreiaDPS.ico");
        return s is null ? SystemIcons.Application : new Icon(s, size, size);
    }

    private static Font Pick(string family, string fallback, float size)
    {
        var f = new Font(family, size);
        return f.Name == family ? f : new Font(fallback, size);
    }

    /// <summary>Cores tiradas dos emblemas de classe do jogo.</summary>
    public static Color ClassColor(GameClass c) => c switch
    {
        GameClass.Gladiator => Color.FromArgb(0x1F, 0xA2, 0xC8),
        GameClass.Templar => Color.FromArgb(0x3B, 0x7C, 0xE6),
        GameClass.Ranger => Color.FromArgb(0x1C, 0xAE, 0x7E),
        GameClass.Assassin => Color.FromArgb(0x46, 0xB8, 0x4E),
        GameClass.Sorcerer => Color.FromArgb(0x86, 0x4C, 0xE0),
        GameClass.Elementalist => Color.FromArgb(0xC2, 0x3F, 0xC4),
        GameClass.Cleric => Color.FromArgb(0xD8, 0xA2, 0x2A),
        GameClass.Chanter => Color.FromArgb(0xEA, 0x7D, 0x2C),
        GameClass.Brawler => Color.FromArgb(0xD6, 0x33, 0x3E),
        _ => Color.FromArgb(0x5E, 0x6B, 0x84),
    };

    public static string ClassName(GameClass c) => c == GameClass.Unknown ? "Unknown" : c.ToString();

    public static string Fmt(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:0.##}B",
        >= 1_000_000 => $"{v / 1_000_000:0.##}M",
        >= 10_000 => $"{v / 1_000:0.#}k",
        _ => $"{v:0}",
    };

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        if (radius <= 0 || r.Width < d || r.Height < d)
        {
            p.AddRectangle(r);
            return p;
        }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, Color c, RectangleF r, float radius)
    {
        using var b = new SolidBrush(c);
        using var p = Rounded(r, radius);
        g.FillPath(b, p);
    }

    public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags align = TextFormatFlags.Left)
    {
        TextRenderer.DrawText(g, text, font, r, color,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    }

    public static int Measure(string text, Font font) =>
        TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
}
