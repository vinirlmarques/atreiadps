using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace AionDps.UI;

/// <summary>Base dos controles desenhados à mão no tema do overlay.</summary>
internal abstract class PaintedControl : Control
{
    protected bool Hover;

    protected PaintedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Hover = false;
        Invalidate();
    }

    protected static Graphics Prepare(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return g;
    }
}

/// <summary>Caixa arredondada que hospeda um TextBox sem borda (texto ou atalho).</summary>
internal sealed class FieldBox : PaintedControl
{
    public TextBox Input { get; }

    public FieldBox(TextBox input)
    {
        Input = input;
        Input.BorderStyle = BorderStyle.None;
        Input.BackColor = Theme.SurfaceHi;
        Input.ForeColor = Theme.Text;
        Input.Font = Theme.Ui;
        Input.GotFocus += (_, _) => Invalidate();
        Input.LostFocus += (_, _) => Invalidate();
        Controls.Add(Input);
        Height = 30;
        Cursor = Cursors.IBeam;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Input.Bounds = new Rectangle(10, (Height - Input.PreferredHeight) / 2 + 1, Width - 20, Input.PreferredHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Input.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Theme.FillRounded(g, Theme.SurfaceHi, r, 6);
        using var pen = new Pen(Input.Focused ? Theme.Accent : Hover ? Theme.TextFaint : Theme.Border);
        using var path = Theme.Rounded(r, 6);
        g.DrawPath(pen, path);
    }
}

/// <summary>Número com botões − / + (e roda do mouse).</summary>
internal sealed class Stepper : PaintedControl
{
    private decimal _value;
    public decimal Minimum { get; init; }
    public decimal Maximum { get; init; } = 100;
    public decimal Step { get; init; } = 1;
    public string Format { get; init; } = "0";
    public string Suffix { get; init; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public decimal Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, Minimum, Maximum);
            Invalidate();
        }
    }

    public Stepper() => Height = 30;

    private Rectangle Minus => new(Width - 58, 3, 26, Height - 6);
    private Rectangle Plus => new(Width - 29, 3, 26, Height - 6);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Minus.Contains(e.Location)) Value -= Step;
        else if (Plus.Contains(e.Location)) Value += Step;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += Math.Sign(e.Delta) * Step;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Theme.FillRounded(g, Theme.SurfaceHi, r, 6);
        using (var pen = new Pen(Hover ? Theme.TextFaint : Theme.Border))
        using (var path = Theme.Rounded(r, 6))
            g.DrawPath(pen, path);
        Theme.DrawText(g, _value.ToString(Format) + Suffix, Theme.Num, Theme.Text, new Rectangle(10, 0, Width - 70, Height));
        var mouse = PointToClient(MousePosition);
        foreach (var (rect, glyph) in new[] { (Minus, ""), (Plus, "") })
        {
            if (Hover && rect.Contains(mouse)) Theme.FillRounded(g, Theme.Border, rect, 4);
            Theme.DrawText(g, glyph, Theme.IconsSmall, Theme.TextDim, rect, TextFormatFlags.HorizontalCenter);
        }
    }
}

/// <summary>Interruptor liga/desliga com o texto ao lado (a linha inteira é clicável).</summary>
internal sealed class ToggleSwitch : PaintedControl
{
    private bool _checked;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            _checked = value;
            Invalidate();
        }
    }

    public ToggleSwitch(string text, bool value)
    {
        Text = text;
        _checked = value;
        Height = 26;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        Theme.DrawText(g, Text, Theme.Ui, Hover ? Theme.Text : Theme.TextDim, new Rectangle(0, 0, Width - 50, Height));
        var track = new RectangleF(Width - 38, (Height - 20) / 2f, 36, 20);
        Theme.FillRounded(g, _checked ? Theme.Accent : Theme.Border, track, 10);
        var kx = _checked ? track.Right - 18 : track.X + 2;
        using var knob = new SolidBrush(_checked ? Theme.Bg : Theme.TextDim);
        g.FillEllipse(knob, kx, track.Y + 2, 16, 16);
    }
}

/// <summary>Barra deslizante com o valor à direita.</summary>
internal sealed class Slider : PaintedControl
{
    private int _value;
    private bool _drag;
    public int Minimum { get; init; }
    public int Maximum { get; init; } = 100;
    public string Suffix { get; init; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, Minimum, Maximum);
            Invalidate();
        }
    }

    public Slider()
    {
        Height = 30;
        Cursor = Cursors.Hand;
    }

    private RectangleF Track => new(8, Height / 2f - 2, Width - 64, 4);

    private void SetFromX(int x)
    {
        var t = Track;
        Value = Minimum + (int)Math.Round((Maximum - Minimum) * Math.Clamp((x - t.X) / t.Width, 0, 1));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _drag = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += Math.Sign(e.Delta) * 5;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        var t = Track;
        Theme.FillRounded(g, Theme.Border, t, 2);
        var frac = (float)(_value - Minimum) / Math.Max(1, Maximum - Minimum);
        Theme.FillRounded(g, Theme.Accent, t with { Width = Math.Max(4, t.Width * frac) }, 2);
        var kx = t.X + t.Width * frac;
        using (var knob = new SolidBrush(Theme.Text)) g.FillEllipse(knob, kx - 7, Height / 2f - 7, 14, 14);
        using (var ring = new Pen(Theme.Accent, 2)) g.DrawEllipse(ring, kx - 7, Height / 2f - 7, 14, 14);
        Theme.DrawText(g, _value + Suffix, Theme.Num, Theme.Text, new Rectangle(Width - 50, 0, 50, Height), TextFormatFlags.Right);
    }
}

/// <summary>Botão arredondado: dourado (principal) ou neutro.</summary>
internal sealed class FlatBtn : PaintedControl
{
    public bool Primary { get; init; }

    public FlatBtn(string text)
    {
        Text = text;
        Size = new Size(96, 32);
        Cursor = Cursors.Hand;
        Font = Theme.UiBold;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e);
        var bg = Primary ? (Hover ? ControlPaint.Light(Theme.Accent, 0.25f) : Theme.Accent) : (Hover ? Theme.Border : Theme.SurfaceHi);
        Theme.FillRounded(g, bg, new RectangleF(0, 0, Width, Height), 7);
        Theme.DrawText(g, Text, Theme.UiBold, Primary ? Theme.Bg : Theme.Text, ClientRectangle, TextFormatFlags.HorizontalCenter);
    }
}
