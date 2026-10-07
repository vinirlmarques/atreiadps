using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AionDps.UI;

/// <summary>Configurações no mesmo visual do overlay: personagem, atalhos, ritmo do boss, filtros e opacidade.</summary>
public sealed class SettingsDialog : Form
{
    private const int HeaderH = 44, Pad = 20, Radius = 12, FieldX = 168;

    private readonly Settings _settings;
    private readonly HotkeyBox _toggle, _reset;
    private readonly TextBox _target, _name;
    private readonly Stepper _tolerance;
    private readonly Slider _opacity;
    private readonly ToggleSwitch _bossOnly, _partyOnly;
    private readonly Label _error;
    private Rectangle _closeRect;
    private bool _closeHover;
    private int _y = HeaderH + 14;

    public SettingsDialog(Settings settings)
    {
        _settings = settings;
        Text = "Configurações · AtreiaDPS";
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        DoubleBuffered = true;
        ClientSize = new Size(380, 100);

        Section("PERSONAGEM");
        _name = Field("Meu personagem", new TextBox { Text = settings.CharacterName });
        Hint("Usado até o jogo mandar seu nome, e para buscar GS/CP no site oficial.");

        Section("ATALHOS");
        _toggle = Field("Ocultar / mostrar", new HotkeyBox(settings.ToggleKey));
        _reset = Field("Limpar dano", new HotkeyBox(settings.ResetKey));
        Hint("Clique no campo e aperte a combinação. Backspace = sem atalho.");

        Section("RITMO DO BOSS");
        _target = Field("Tempo-alvo (m:ss)", new TextBox { Text = Settings.FormatTime(settings.TargetSeconds) });
        _tolerance = Place("Tolerância", new Stepper
        {
            Minimum = 0, Maximum = 20, Step = 0.5m, Format = "0.0", Suffix = " %", Value = (decimal)settings.PaceTolerance,
        });
        PaceLegend();

        Section("FILTROS");
        _bossOnly = Toggle("Só lutas de boss", settings.BossOnly);
        _partyOnly = Toggle("Só meu grupo (sozinho: só você)", settings.PartyOnly);

        Section("APARÊNCIA");
        _opacity = Place("Opacidade", new Slider { Minimum = 30, Maximum = 100, Suffix = "%", Value = settings.OpacityPct });

        _y += 16;
        _error = new Label { Location = new Point(Pad, _y + 8), AutoSize = true, ForeColor = Theme.Bad, BackColor = Theme.Bg };
        Controls.Add(_error);
        var cancel = new FlatBtn("Cancelar");
        cancel.Location = new Point(ClientSize.Width - Pad - cancel.Width, _y);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        var save = new FlatBtn("Salvar") { Primary = true };
        save.Location = new Point(cancel.Left - 8 - save.Width, _y);
        save.Click += (_, _) => Save();
        Controls.Add(save);
        Controls.Add(cancel);
        ClientSize = new Size(ClientSize.Width, _y + save.Height + Pad);
        ActiveControl = null;
    }

    // ------------------------------------------------------------ janela

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = Theme.Rounded(new RectangleF(0, 0, Width, Height), Radius);
        Region = new Region(path);
        _closeRect = new Rectangle(Width - 12 - 30, 8, 30, 28);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var hb = new LinearGradientBrush(new Rectangle(0, 0, Width, HeaderH), Theme.SurfaceHi, Theme.Surface, 90f))
            g.FillRectangle(hb, 0, 0, Width, HeaderH);
        using (var line = new Pen(Theme.Border)) g.DrawLine(line, 0, HeaderH - 1, Width, HeaderH - 1);

        Theme.DrawText(g, "", Theme.Icons, Theme.Accent, new Rectangle(Pad - 2, 0, 20, HeaderH), TextFormatFlags.HorizontalCenter);
        Theme.DrawText(g, "Configurações", Theme.UiBold, Theme.Text, new Rectangle(Pad + 24, 0, 200, HeaderH));
        if (_closeHover) Theme.FillRounded(g, Color.FromArgb(40, 255, 255, 255), _closeRect, 5);
        Theme.DrawText(g, "", Theme.IconsSmall, _closeHover ? Theme.Text : Theme.TextDim, _closeRect, TextFormatFlags.HorizontalCenter);

        using var pen = new Pen(Theme.Border);
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius);
        g.DrawPath(pen, path);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = _closeRect.Contains(e.Location);
        if (hover == _closeHover) return;
        _closeHover = hover;
        Invalidate(_closeRect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_closeRect.Contains(e.Location)) DialogResult = DialogResult.Cancel;
        else if (e.Y < HeaderH)
        {
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0); // arrastar pela barra
        }
    }

    private void Save()
    {
        if (!Settings.TryParseTime(_target.Text, out var secs))
        {
            _error.Text = "Tempo-alvo inválido (ex: 5:00)";
            return;
        }
        if (_name.Text.Trim() != _settings.CharacterName) _settings.CharacterServer = 0; // trocou de personagem
        _settings.CharacterName = _name.Text.Trim();
        _settings.ToggleKey = _toggle.Value;
        _settings.ResetKey = _reset.Value;
        _settings.TargetSeconds = secs;
        _settings.PaceTolerance = (double)_tolerance.Value;
        _settings.BossOnly = _bossOnly.Checked;
        _settings.PartyOnly = _partyOnly.Checked;
        _settings.OpacityPct = _opacity.Value;
        DialogResult = DialogResult.OK;
    }

    // ------------------------------------------------------------ layout

    private void Section(string title)
    {
        if (_y > HeaderH + 14) _y += 12;
        Controls.Add(new Label { Text = title, Font = Theme.UiTiny, ForeColor = Theme.Accent, BackColor = Theme.Bg, Location = new Point(Pad, _y), AutoSize = true });
        Controls.Add(new Panel { BackColor = Theme.Border, Location = new Point(Pad, _y + 18), Size = new Size(ClientSize.Width - 2 * Pad, 1) });
        _y += 28;
    }

    private T Field<T>(string label, T input) where T : TextBox
    {
        Place(label, new FieldBox(input));
        return input;
    }

    private T Place<T>(string label, T control) where T : Control
    {
        Controls.Add(new Label { Text = label, ForeColor = Theme.TextDim, BackColor = Theme.Bg, Location = new Point(Pad, _y + 7), AutoSize = true });
        control.Location = new Point(FieldX, _y);
        control.Width = ClientSize.Width - FieldX - Pad;
        Controls.Add(control);
        _y += control.Height + 8;
        return control;
    }

    private ToggleSwitch Toggle(string text, bool value)
    {
        var t = new ToggleSwitch(text, value) { Location = new Point(Pad, _y), Width = ClientSize.Width - 2 * Pad };
        Controls.Add(t);
        _y += t.Height + 6;
        return t;
    }

    private void Hint(string text)
    {
        var label = new Label
        {
            Text = text, Font = Theme.UiSmall, ForeColor = Theme.TextFaint, BackColor = Theme.Bg, Location = new Point(Pad, _y - 2),
            MaximumSize = new Size(ClientSize.Width - 2 * Pad, 0), AutoSize = true,
        };
        Controls.Add(label);
        _y += label.PreferredHeight + 2;
    }

    /// <summary>Legenda da bolinha de ritmo com as três cores.</summary>
    private void PaceLegend()
    {
        var legend = new PaceLegendControl { Location = new Point(Pad, _y - 2), Size = new Size(ClientSize.Width - 2 * Pad, 18) };
        Controls.Add(legend);
        _y += legend.Height + 2;
    }

    private sealed class PaceLegendControl : PaintedControl
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Prepare(e);
            var x = 0;
            foreach (var (color, text) in new[] { (Theme.Warn, "no ritmo"), (Theme.Good, "adiantado"), (Theme.Bad, "atrasado") })
            {
                using (var b = new SolidBrush(color)) g.FillEllipse(b, x, Height / 2 - 4, 8, 8);
                Theme.DrawText(g, text, Theme.UiSmall, Theme.TextFaint, new Rectangle(x + 12, 0, 80, Height));
                x += 12 + Theme.Measure(text, Theme.UiSmall) + 16;
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    /// <summary>Campo que grava a próxima combinação de teclas pressionada.</summary>
    private sealed class HotkeyBox : TextBox
    {
        public Keys Value { get; private set; }

        public HotkeyBox(Keys initial)
        {
            ReadOnly = true;
            Cursor = Cursors.Hand;
            ShortcutsEnabled = false;
            SetValue(initial);
        }

        private void SetValue(Keys k)
        {
            Value = k;
            Text = Settings.Describe(k);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            BackColor = Theme.SurfaceHi; // ReadOnly mudaria a cor
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var code = keyData & Keys.KeyCode;
            var plain = (keyData & Keys.Modifiers) == 0;
            if (code is Keys.Back or Keys.Delete && plain)
            {
                SetValue(Keys.None);
                return true;
            }
            if (code is Keys.Tab or Keys.Enter or Keys.Escape && plain) return base.ProcessCmdKey(ref msg, keyData);
            if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
            {
                Text = string.Join("+", Settings.Modifiers(keyData)) + "+…";
                return true;
            }
            SetValue(keyData);
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            Text = Settings.Describe(Value);
        }
    }
}
