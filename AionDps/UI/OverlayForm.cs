using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using AionDps.Capture;
using AionDps.Meter;
using AionDps.Protocol;

namespace AionDps.UI;

/// <summary>
/// Overlay sempre no topo, sem borda, que não rouba o foco do jogo. Redimensionável pelas bordas.
/// Clique num jogador = skills · botão direito = menu · arraste pelo cabeçalho/rodapé · roda do mouse = rolar.
/// </summary>
public sealed class OverlayForm : Form
{
    private const int HeaderH = 46, FooterH = 24, PlayerH = 38, RowGap = 2, SkillH = 24, ColHeadH = 20;
    private const int IconSize = 26, Pad = 12, Radius = 10, IconBtn = 28, Grip = 7;
    private const int HotkeyReset = 1, HotkeyToggle = 2;

    private readonly CombatMeter _meter;
    private readonly GameCapture _capture;
    private readonly Settings _settings = Settings.Load();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly ToolTip _tip = new() { InitialDelay = 300 };
    private readonly Dictionary<GameClass, Image> _icons = LoadIcons();

    private Snapshot? _snap;
    private uint? _detailId;
    private int _scroll;
    private Point _mouse = new(-1, -1);
    private string? _tipShown;
    private readonly List<HitBox> _hits = new();

    private sealed record HitBox(Rectangle Rect, Action Click, string? Tip);

    /// <summary>Snapshot fixo (só para gerar prévia de layout).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Snapshot? Preview { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public uint? PreviewDetail { get => _detailId; set => _detailId = value; }

    public OverlayForm(CombatMeter meter, GameCapture capture)
    {
        _meter = meter;
        _capture = capture;
        Text = "AtreiaDPS";
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(280, HeaderH + FooterH + PlayerH + 8);
        Bounds = FitOnScreen(new Rectangle(_settings.X, _settings.Y, _settings.Width, _settings.Height));
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Theme.Bg;
        DoubleBuffered = true;
        ApplySettings();

        ContextMenuStrip = BuildMenu();
        _tray = new NotifyIcon { Icon = Theme.AppIcon(16), Text = "AtreiaDPS", Visible = true, ContextMenuStrip = BuildMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleVisible(); };

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void ApplySettings()
    {
        _meter.BossOnly = _settings.BossOnly;
        _meter.PartyOnly = _settings.PartyOnly;
        _meter.FallbackSelfName = _settings.CharacterName;
        _meter.FallbackSelfServer = _settings.CharacterServer;
        Opacity = Math.Clamp(_settings.OpacityPct, 30, 100) / 100.0;
    }

    private static Rectangle FitOnScreen(Rectangle r)
    {
        var area = Screen.FromRectangle(r).WorkingArea;
        r.Width = Math.Min(r.Width, area.Width);
        r.Height = Math.Min(r.Height, area.Height);
        r.X = Math.Clamp(r.X, area.Left, area.Right - r.Width);
        r.Y = Math.Clamp(r.Y, area.Top, area.Bottom - r.Height);
        return r;
    }

    // ------------------------------------------------------------ janela

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Tick();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkeys();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), Radius);
        Region = new Region(path);
        Invalidate();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        SaveBounds();
    }

    private void SaveBounds()
    {
        (_settings.X, _settings.Y, _settings.Width, _settings.Height) = (Left, Top, Width, Height);
        _settings.Save();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x0084: // WM_NCHITTEST: bordas e cantos redimensionam
                base.WndProc(ref m);
                var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                if (ResizeHit(p) is { } hit) m.Result = hit;
                return;
            case 0x0312: // WM_HOTKEY
                if (m.WParam == HotkeyReset) ClearDamage();
                else if (m.WParam == HotkeyToggle) ToggleVisible();
                break;
        }
        base.WndProc(ref m);
    }

    private IntPtr? ResizeHit(Point p)
    {
        bool l = p.X < Grip, r = p.X >= Width - Grip, t = p.Y < Grip / 2, b = p.Y >= Height - Grip;
        var corner = p.X >= Width - 16 && p.Y >= Height - 16; // alça visível no canto
        return (l, r, t, b) switch
        {
            _ when corner => 17,          // HTBOTTOMRIGHT
            (true, _, true, _) => 13,     // HTTOPLEFT
            (_, true, true, _) => 14,     // HTTOPRIGHT
            (true, _, _, true) => 16,     // HTBOTTOMLEFT
            (_, true, _, true) => 17,     // HTBOTTOMRIGHT
            (true, _, _, _) => 10,        // HTLEFT
            (_, true, _, _) => 11,        // HTRIGHT
            (_, _, true, _) => 12,        // HTTOP
            (_, _, _, true) => 15,        // HTBOTTOM
            _ => null,
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyReset);
        UnregisterHotKey(Handle, HotkeyToggle);
        SaveBounds();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------ atalhos e ações

    private List<string> RegisterHotkeys()
    {
        var failed = new List<string>();
        UnregisterHotKey(Handle, HotkeyReset);
        UnregisterHotKey(Handle, HotkeyToggle);
        if (!Register(HotkeyReset, _settings.ResetKey)) failed.Add(Settings.Describe(_settings.ResetKey));
        if (!Register(HotkeyToggle, _settings.ToggleKey)) failed.Add(Settings.Describe(_settings.ToggleKey));
        return failed;
    }

    private bool Register(int id, Keys k)
    {
        if ((k & Keys.KeyCode) == Keys.None) return true;
        uint mods = 0x4000; // MOD_NOREPEAT
        if (k.HasFlag(Keys.Alt)) mods |= 0x1;
        if (k.HasFlag(Keys.Control)) mods |= 0x2;
        if (k.HasFlag(Keys.Shift)) mods |= 0x4;
        return RegisterHotKey(Handle, id, mods, (uint)(k & Keys.KeyCode));
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _settings.Save();
        ApplySettings();
        var failed = RegisterHotkeys();
        if (failed.Count > 0)
            MessageBox.Show($"Não deu para registrar: {string.Join(", ", failed)}.\nOutro programa já usa essa combinação.",
                "Atalhos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Tick();
    }

    private void ClearDamage()
    {
        _meter.Reset();
        _detailId = null;
        _scroll = 0;
        Tick();
    }

    private void ToggleVisible()
    {
        if (Visible) Hide();
        else
        {
            Show();
            TopMost = true;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
        var toggle = new ToolStripMenuItem("Ocultar / mostrar", null, (_, _) => ToggleVisible());
        var reset = new ToolStripMenuItem("Limpar dano", null, (_, _) => ClearDamage());
        menu.Opening += (_, _) =>
        {
            toggle.ShortcutKeyDisplayString = Settings.Describe(_settings.ToggleKey);
            reset.ShortcutKeyDisplayString = Settings.Describe(_settings.ResetKey);
        };
        menu.Items.Add(toggle);
        menu.Items.Add(reset);
        menu.Items.Add("Copiar resultado", null, (_, _) => CopyResult());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configurações…", null, (_, _) => OpenSettings());
        menu.Items.Add("Sair", null, (_, _) => Close());
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Theme.Text;
        return menu;
    }

    private void Tick()
    {
        _snap = Preview ?? _meter.Snapshot(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // Lembra o nome/servidor que o jogo confirmou, para buscar GS/CP na próxima vez mesmo jogando sozinho.
        if (Preview is null && _meter.ConfirmedSelf is { } me &&
            (me.Name != _settings.CharacterName || (me.Server > 0 && me.Server != _settings.CharacterServer)))
        {
            _settings.CharacterName = me.Name;
            if (me.Server > 0) _settings.CharacterServer = me.Server;
            _settings.Save();
        }
        Invalidate();
    }

    // ------------------------------------------------------------ pintura

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _hits.Clear();

        var s = _snap;
        DrawHeader(g, s);

        var body = new Rectangle(0, HeaderH, Width, Height - HeaderH - FooterH);
        g.SetClip(body);
        if (s is null || s.Rows.Count == 0) DrawEmpty(g, body);
        else if (_detailId is { } id && s.Rows.FirstOrDefault(r => r.Id == id) is { } p) DrawDetail(g, p, body);
        else
        {
            _detailId = null;
            DrawRows(g, s, body);
        }
        g.ResetClip();

        DrawFooter(g, s);

        // contorno
        using var pen = new Pen(Theme.Border);
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius);
        g.DrawPath(pen, path);

        UpdateTooltip();
    }

    private void DrawHeader(Graphics g, Snapshot? s)
    {
        using (var hb = new LinearGradientBrush(new Rectangle(0, 0, Width, HeaderH), Theme.SurfaceHi, Theme.Surface, 90f))
            g.FillRectangle(hb, 0, 0, Width, HeaderH);
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, HeaderH - 1, Width, HeaderH - 1);

        // botões à direita
        var gear = new Rectangle(Width - Pad - IconBtn + 6, 4, IconBtn, 26);
        var clear = new Rectangle(gear.Left - IconBtn, 4, IconBtn, 26);
        IconButton(g, "", gear, "Configurações", OpenSettings);
        IconButton(g, "", clear, $"Limpar dano ({Settings.Describe(_settings.ResetKey)})", ClearDamage);
        var right = clear.Left - 4;

        var x = Pad;
        if (s is null)
        {
            var brand = Theme.Measure("Atreia", Theme.NumBig);
            Theme.DrawText(g, "Atreia", Theme.NumBig, Theme.Accent, new Rectangle(x, 4, brand + 2, 26));
            Theme.DrawText(g, "DPS", Theme.NumBig, Theme.Text, new Rectangle(x + brand, 4, 50, 26));
            DrawHpBar(g, null, null);
            return;
        }

        // ● tempo
        using (var db = new SolidBrush(s.Active ? Theme.Good : Theme.TextFaint)) g.FillEllipse(db, x, 14, 7, 7);
        x += 13;
        var t = TimeSpan.FromSeconds(s.FightSeconds);
        var time = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        Theme.DrawText(g, time, Theme.NumBig, Theme.Text, new Rectangle(x, 4, 60, 26));
        x += Theme.Measure(time, Theme.NumBig) + 10;

        // bolinha de ritmo (direita, antes dos botões)
        if (Pace(s) is { } pace)
        {
            var label = $"{(pace.Diff >= 0 ? "+" : "")}{pace.Diff:0.0}%";
            var w = Theme.Measure(label, Theme.NumSmall) + 24;
            var pill = new Rectangle(right - w, 9, w, 18);
            Theme.FillRounded(g, Color.FromArgb(38, pace.Color), pill, 9);
            using (var pb = new SolidBrush(pace.Color)) g.FillEllipse(pb, pill.X + 7, pill.Y + 5, 8, 8);
            Theme.DrawText(g, label, Theme.NumSmall, pace.Color, new Rectangle(pill.X + 18, pill.Y, w - 20, pill.Height));
            _hits.Add(new HitBox(pill, () => { }, pace.Tip));
            right = pill.Left - 6;
        }

        // nome do boss + HP%
        var hpText = s.TargetHpPct is { } hp ? $"{hp:0.0}%" : "";
        var hpW = hpText.Length > 0 ? Theme.Measure(hpText, Theme.NumSmall) + 6 : 0;
        var nameRect = new Rectangle(x, 4, Math.Max(0, right - x - hpW), 26);
        Theme.DrawText(g, s.Target ?? "Combate", Theme.UiBold, Theme.Accent, nameRect);
        if (hpW > 0)
        {
            var nx = x + Math.Min(Theme.Measure(s.Target ?? "", Theme.UiBold), nameRect.Width) + 6;
            Theme.DrawText(g, hpText, Theme.NumSmall, Theme.TextDim, new Rectangle(nx, 5, hpW, 26));
        }

        DrawHpBar(g, s.TargetHpPct, Pace(s));
    }

    /// <summary>Barra de HP do boss; a marca branca mostra onde o HP deveria estar pelo tempo-alvo.</summary>
    private void DrawHpBar(Graphics g, double? hpPct, PaceInfo? pace)
    {
        var bar = new RectangleF(Pad, 33, Width - 2 * Pad, 5);
        Theme.FillRounded(g, Theme.Bg, bar, 2.5f);
        if (hpPct is not { } hp) return;
        var fill = bar with { Width = (float)(bar.Width * Math.Clamp(hp, 0, 100) / 100) };
        if (fill.Width > 1)
        {
            using var b = new LinearGradientBrush(bar, Color.FromArgb(150, 40, 48), Theme.HpFill, 0f);
            using var path = Theme.Rounded(fill, 2.5f);
            g.FillPath(b, path);
        }
        if (pace is { } p)
        {
            var mx = bar.X + bar.Width * (float)Math.Clamp(p.ExpectedHp, 0, 100) / 100;
            using var mark = new SolidBrush(Color.FromArgb(230, 255, 255, 255));
            g.FillRectangle(mark, mx - 1, bar.Y - 2, 2, bar.Height + 4);
        }
    }

    private readonly record struct PaceInfo(double Diff, double ExpectedHp, Color Color, string Tip);

    /// <summary>Compara % de vida perdida com % do tempo-alvo passado.</summary>
    private PaceInfo? Pace(Snapshot s)
    {
        if (s.BossHpLostPct is not { } lost || _settings.TargetSeconds <= 0) return null;
        var timePct = 100.0 * s.FightSeconds / _settings.TargetSeconds;
        var diff = lost - timePct;
        var color = Math.Abs(diff) <= _settings.PaceTolerance ? Theme.Warn : diff < 0 ? Theme.Bad : Theme.Good;
        var start = (s.TargetHpPct ?? 0) + lost;
        var tip = $"Vida perdida {lost:0.0}% · tempo {timePct:0.0}% de {Settings.FormatTime(_settings.TargetSeconds)}";
        return new PaceInfo(diff, start - timePct, color, tip);
    }

    private void DrawEmpty(Graphics g, Rectangle body)
    {
        var msg = _meter.BossOnly ? "Aguardando luta de boss" : "Aguardando combate";
        var hint = _meter.BossOnly ? "A luta começa quando seu grupo acerta um boss" : "A luta começa com o seu primeiro golpe";
        var cy = body.Top + body.Height / 2;
        Theme.DrawText(g, "", Theme.Icons, Theme.TextFaint, new Rectangle(0, cy - 34, Width, 20), TextFormatFlags.HorizontalCenter);
        Theme.DrawText(g, msg, Theme.UiBold, Theme.TextDim, new Rectangle(0, cy - 12, Width, 18), TextFormatFlags.HorizontalCenter);
        Theme.DrawText(g, hint, Theme.UiSmall, Theme.TextFaint, new Rectangle(Pad, cy + 6, Width - 2 * Pad, 16), TextFormatFlags.HorizontalCenter);
    }

    private void DrawRows(Graphics g, Snapshot s, Rectangle body)
    {
        var visible = Math.Max(1, (body.Height - 4) / (PlayerH + RowGap));
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, s.Rows.Count - visible));
        var top = s.Rows[0].Damage;
        var y = body.Top + 4;
        for (var i = _scroll; i < s.Rows.Count && y < body.Bottom; i++)
        {
            var r = s.Rows[i];
            var w = top == 0 ? 0 : (int)((Width - 8) * (double)r.Damage / top);
            var rect = new Rectangle(4, y, Width - 8, PlayerH);
            DrawPlayer(g, r, rect, w, i + 1, Theme.Fmt(r.Damage), $"· {r.Share * 100:0.0}%");
            var id = r.Id;
            _hits.Add(new HitBox(rect, () => { _detailId = id; _scroll = 0; }, null));
            y += PlayerH + RowGap;
        }
        DrawScrollbar(g, body, s.Rows.Count, visible);
    }

    private void DrawScrollbar(Graphics g, Rectangle body, int total, int visible)
    {
        if (total <= visible) return;
        var track = body.Height - 8;
        var h = Math.Max(16, track * visible / total);
        var y = body.Top + 4 + (track - h) * _scroll / Math.Max(1, total - visible);
        Theme.FillRounded(g, Color.FromArgb(90, Theme.TextDim), new RectangleF(Width - 4, y, 2.5f, h), 1.25f);
    }

    /// <summary>Linha de jogador: faixa e barra na cor da classe, emblema, nome/GS/CP à esquerda, DPS à direita.</summary>
    private void DrawPlayer(Graphics g, PlayerRow r, Rectangle rect, int barWidth, int? rank, string sub, string? subExtra, bool back = false)
    {
        var c = Theme.ClassColor(r.Class);
        var hover = rect.Contains(_mouse);
        Theme.FillRounded(g, hover ? Theme.SurfaceHi : Theme.Surface, rect, 6);
        if (barWidth > 2)
        {
            var bar = rect with { Width = barWidth };
            using var b = new LinearGradientBrush(bar, Color.FromArgb(r.IsSelf ? 120 : 95, c), Color.FromArgb(r.IsSelf ? 55 : 35, c), 0f);
            using var path = Theme.Rounded(bar, 6);
            g.FillPath(b, path);
        }
        using (var stripe = new SolidBrush(c)) g.FillRectangle(stripe, rect.X, rect.Y + 6, 3, rect.Height - 12);

        var x = rect.X + 10;
        if (back)
        {
            Theme.DrawText(g, "", Theme.Icons, Theme.TextDim, new Rectangle(x - 2, rect.Y, 16, rect.Height), TextFormatFlags.HorizontalCenter);
            x += 16;
        }
        else if (rank is { } n)
        {
            Theme.DrawText(g, n.ToString(), Theme.NumSmall, Theme.TextFaint, new Rectangle(x - 2, rect.Y, 14, rect.Height), TextFormatFlags.HorizontalCenter);
            x += 16;
        }

        var iy = rect.Y + (rect.Height - IconSize) / 2;
        if (_icons.TryGetValue(r.Class, out var icon)) g.DrawImage(icon, x, iy, IconSize, IconSize);
        else
        {
            using var ub = new SolidBrush(Color.FromArgb(70, Theme.TextDim));
            g.FillEllipse(ub, x + 3, iy + 3, IconSize - 6, IconSize - 6);
        }
        x += IconSize + 8;

        // direita: DPS em cima, dano · % embaixo
        var rightEdge = rect.Right - 10;
        var unitW = Theme.Measure("/s", Theme.NumSmall);
        var dps = Theme.Fmt(r.Dps);
        var dpsW = Theme.Measure(dps, Theme.NumBig);
        Theme.DrawText(g, "/s", Theme.NumSmall, Theme.TextDim, new Rectangle(rightEdge - unitW, rect.Y + 4, unitW, 18));
        Theme.DrawText(g, dps, Theme.NumBig, Theme.Text, new Rectangle(rightEdge - unitW - dpsW - 1, rect.Y + 2, dpsW + 2, 20));
        // embaixo: dano total em destaque + complemento ("· 34,0%")
        var extraW = subExtra is null ? 0 : Theme.Measure(subExtra, Theme.NumSmall) + 2;
        var subW = Theme.Measure(sub, Theme.Num);
        if (subExtra is not null)
            Theme.DrawText(g, subExtra, Theme.NumSmall, Theme.TextDim, new Rectangle(rightEdge - extraW, rect.Y + 21, extraW + 1, 15));
        Theme.DrawText(g, sub, Theme.Num, Color.FromArgb(225, Theme.Text), new Rectangle(rightEdge - extraW - subW - 1, rect.Y + 19, subW + 2, 16));
        var textRight = rightEdge - Math.Max(unitW + dpsW, subW + extraW) - 8;

        // esquerda: nome, GS/CP
        Theme.DrawText(g, r.Name, Theme.UiBold, r.IsSelf ? Theme.Accent : Theme.Text, new Rectangle(x, rect.Y + 3, textRight - x, 18));
        var stats = new List<string>();
        if (r.GearScore is { } gs) stats.Add(r.GearIsItemLevel ? $"IL {gs}" : $"GS {gs}");
        if (r.CombatPower is { } cp) stats.Add($"CP {cp:#,0}".Replace(',', '.'));
        if (stats.Count == 0) stats.Add(Theme.ClassName(r.Class));
        Theme.DrawText(g, string.Join("  ·  ", stats), Theme.UiSmall, Theme.TextDim, new Rectangle(x, rect.Y + 20, textRight - x, 15));
    }

    private void DrawDetail(Graphics g, PlayerRow p, Rectangle body)
    {
        var head = new Rectangle(4, body.Top + 4, Width - 8, PlayerH);
        DrawPlayer(g, p, head, Width - 8, null, Theme.Fmt(p.Damage), $"· crit {p.CritRate * 100:0}%", back: true);
        _hits.Add(new HitBox(head, () => { _detailId = null; _scroll = 0; }, "Voltar"));

        // colunas: SKILL | HITS | CRIT | DANO | %
        int pctX = Width - Pad - 38, dmgX = pctX - 62, critX = dmgX - 42, hitsX = critX - 38;
        var y = head.Bottom + 4;
        var col = Theme.TextFaint;
        Theme.DrawText(g, "SKILL", Theme.UiTiny, col, new Rectangle(Pad, y, hitsX - Pad, ColHeadH));
        Theme.DrawText(g, "HITS", Theme.UiTiny, col, new Rectangle(hitsX, y, 34, ColHeadH), TextFormatFlags.Right);
        Theme.DrawText(g, "CRIT", Theme.UiTiny, col, new Rectangle(critX, y, 38, ColHeadH), TextFormatFlags.Right);
        Theme.DrawText(g, "DANO", Theme.UiTiny, col, new Rectangle(dmgX, y, 58, ColHeadH), TextFormatFlags.Right);
        Theme.DrawText(g, "%", Theme.UiTiny, col, new Rectangle(pctX, y, 38, ColHeadH), TextFormatFlags.Right);
        y += ColHeadH;

        var listTop = y;
        var visible = Math.Max(1, (body.Bottom - listTop) / SkillH);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, p.Skills.Count - visible));
        var top = p.Skills.Count > 0 ? p.Skills[0].Damage : 0;
        var c = Theme.ClassColor(p.Class);
        for (var i = _scroll; i < p.Skills.Count && y < body.Bottom; i++)
        {
            var sk = p.Skills[i];
            var w = top == 0 ? 0 : (int)((Width - 8) * (double)sk.Damage / top);
            if (i % 2 == 0)
                using (var alt = new SolidBrush(Theme.Surface)) g.FillRectangle(alt, 4, y, Width - 8, SkillH);
            using (var bar = new SolidBrush(Color.FromArgb(48, c))) g.FillRectangle(bar, 4, y, w, SkillH);
            Theme.DrawText(g, sk.Name, Theme.Ui, Theme.Text, new Rectangle(Pad, y, hitsX - Pad - 4, SkillH));
            Theme.DrawText(g, sk.Hits.ToString(), Theme.NumSmall, Theme.TextDim, new Rectangle(hitsX, y, 34, SkillH), TextFormatFlags.Right);
            Theme.DrawText(g, $"{sk.CritRate * 100:0}%", Theme.NumSmall, Theme.TextDim, new Rectangle(critX, y, 38, SkillH), TextFormatFlags.Right);
            Theme.DrawText(g, Theme.Fmt(sk.Damage), Theme.Num, Theme.Text, new Rectangle(dmgX, y, 58, SkillH), TextFormatFlags.Right);
            Theme.DrawText(g, $"{sk.Share * 100:0.0}", Theme.NumSmall, Theme.TextDim, new Rectangle(pctX, y, 38, SkillH), TextFormatFlags.Right);
            y += SkillH;
        }
        DrawScrollbar(g, Rectangle.FromLTRB(0, listTop - 4, Width, body.Bottom), p.Skills.Count, visible);
    }

    private void DrawFooter(Graphics g, Snapshot? s)
    {
        var top = Height - FooterH;
        using (var fb = new SolidBrush(Theme.Surface)) g.FillRectangle(fb, 0, top, Width, FooterH);
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, top, Width, top);

        // alça de redimensionar
        using (var dot = new SolidBrush(Theme.TextFaint))
            foreach (var (dx, dy) in new[] { (4, 4), (8, 4), (4, 8), (8, 8), (12, 4), (4, 12) })
                g.FillRectangle(dot, Width - dx - 3, Height - dy - 3, 2, 2);

        // navegação ‹ 1/3 ›
        var right = Width - 22;
        if (s is not null)
        {
            var label = s.View;
            var lw = Theme.Measure(label, Theme.NumSmall) + 6;
            var next = new Rectangle(right - 18, top + 2, 18, FooterH - 4);
            var lbl = new Rectangle(next.Left - lw, top, lw, FooterH);
            var prev = new Rectangle(lbl.Left - 18, top + 2, 18, FooterH - 4);
            IconButton(g, "", prev, "Luta anterior", () => _meter.Step(-1), small: true);
            Theme.DrawText(g, label, Theme.NumSmall, Theme.TextDim, lbl, TextFormatFlags.HorizontalCenter);
            IconButton(g, "", next, "Próxima luta", () => _meter.Step(+1), small: true);
            right = prev.Left - 6;
        }

        // status: ● Capturando · Nome · Grupo N
        var capturing = _capture.Status == "Capturando";
        using (var sb = new SolidBrush(capturing ? Theme.Good : Theme.Warn)) g.FillEllipse(sb, Pad, top + 9, 6, 6);
        var me = _meter.SelfName;
        var parts = new List<string> { _capture.Status, me.Length > 0 ? me : "personagem ?" };
        if (_meter.PartySize > 0) parts.Add($"grupo {_meter.PartySize}");
        if (s is not null && s.Total > 0) parts.Add($"grupo {Theme.Fmt(s.Total)} · {Theme.Fmt(s.Total / s.Seconds)}/s");
        Theme.DrawText(g, string.Join("  ·  ", parts), Theme.UiSmall, Theme.TextDim, new Rectangle(Pad + 12, top, right - Pad - 12, FooterH));
    }

    private void IconButton(Graphics g, string glyph, Rectangle r, string tip, Action click, bool small = false)
    {
        var hover = r.Contains(_mouse);
        if (hover) Theme.FillRounded(g, Color.FromArgb(40, 255, 255, 255), r, 5);
        Theme.DrawText(g, glyph, small ? Theme.IconsSmall : Theme.Icons, hover ? Theme.Text : Theme.TextDim,
            r, TextFormatFlags.HorizontalCenter);
        _hits.Add(new HitBox(r, click, tip));
    }

    private void UpdateTooltip()
    {
        var tip = _hits.LastOrDefault(h => h.Tip is not null && h.Rect.Contains(_mouse))?.Tip;
        if (tip == _tipShown) return;
        _tipShown = tip;
        if (tip is null) _tip.Hide(this);
        else _tip.Show(tip, this, _mouse.X + 12, _mouse.Y + 18, 3000);
    }

    // ------------------------------------------------------------ mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll -= Math.Sign(e.Delta);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        foreach (var h in Enumerable.Reverse(_hits))
        {
            if (!h.Rect.Contains(e.Location)) continue;
            h.Click();
            Tick();
            return;
        }
        if (e.Y < HeaderH || e.Y > Height - FooterH)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
        }
    }

    // ------------------------------------------------------------ util

    private void CopyResult()
    {
        var s = _snap;
        if (s is null) return;
        var sb = new StringBuilder();
        sb.Append($"[DPS {TimeSpan.FromSeconds(s.Seconds):m\\:ss}{(s.Target is null ? "" : " " + s.Target)}] ");
        var i = 0;
        foreach (var r in s.Rows.Take(8)) sb.Append($"{++i}.{r.Name} {Theme.Fmt(r.Dps)}/s ({r.Share * 100:0}%) ");
        try { Clipboard.SetText(sb.ToString().TrimEnd()); } catch { }
    }

    private static Dictionary<GameClass, Image> LoadIcons()
    {
        var icons = new Dictionary<GameClass, Image>();
        var dir = Path.Combine(AppContext.BaseDirectory, "data", "classes");
        foreach (var c in Enum.GetValues<GameClass>())
        {
            var file = Path.Combine(dir, c.ToString().ToLowerInvariant() + ".png");
            try
            {
                if (File.Exists(file)) icons[c] = Image.FromStream(new MemoryStream(File.ReadAllBytes(file)));
            }
            catch { /* ícone faltando: usa círculo */ }
        }
        return icons;
    }

    /// <summary>Menu de contexto no tema escuro.</summary>
    private sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Theme.Text;
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.SurfaceHi;
        public override Color MenuItemSelected => Theme.SurfaceHi;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
