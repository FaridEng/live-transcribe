using Android.Content;
using Android.Graphics;
using Android.Views;

namespace LiveTranscribe.Droid.Overlay;

public enum BubbleLook { Idle, Listening, Processing, Error }

/// <summary>
/// The round button, drawn by hand so it needs no image assets: a microphone at rest, a red disc with a
/// level ring while listening, a turning arc while processing. An "EN" badge marks translation.
/// </summary>
public sealed class BubbleView : View
{
    private static readonly Color IdleColor = Color.ParseColor("#263238");
    private static readonly Color EnglishColor = Color.ParseColor("#1565C0");
    private static readonly Color ListeningColor = Color.ParseColor("#D32F2F");
    private static readonly Color ProcessingColor = Color.ParseColor("#546E7A");
    private static readonly Color ErrorColor = Color.ParseColor("#E65100");

    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round };
    private readonly Paint _text = new(PaintFlags.AntiAlias) { TextAlign = Paint.Align.Center, FakeBoldText = true };
    private float _level;
    private float _spin;

    public BubbleView(Context context) : base(context)
    {
        _stroke.SetStyle(Paint.Style.Stroke);
    }

    public BubbleLook Look { get; private set; }

    public bool English { get; private set; }

    public void Show(BubbleLook look, bool english)
    {
        Look = look;
        English = english;
        if (look != BubbleLook.Listening)
        {
            _level = 0;
        }

        Invalidate();
    }

    /// <summary>0..1, from the controller's level meter.</summary>
    public void SetLevel(float level)
    {
        // Fast attack, slow release, so the ring reads as speech rather than flicker.
        _level = level > _level ? level : _level * 0.8f + level * 0.2f;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        var size = Math.Min(Width, Height);
        var cx = Width / 2f;
        var cy = Height / 2f;
        var radius = size / 2f * 0.78f;

        if (Look == BubbleLook.Listening)
        {
            _fill.Color = new Color(ListeningColor.R, ListeningColor.G, ListeningColor.B, (byte)70);
            canvas.DrawCircle(cx, cy, radius + (size / 2f - radius) * Math.Clamp(_level * 1.6f, 0.15f, 1f), _fill);
        }

        _fill.Color = Look switch
        {
            BubbleLook.Listening => ListeningColor,
            BubbleLook.Processing => ProcessingColor,
            BubbleLook.Error => ErrorColor,
            _ => English ? EnglishColor : IdleColor
        };
        canvas.DrawCircle(cx, cy, radius, _fill);

        if (Look == BubbleLook.Processing)
        {
            _stroke.Color = Color.White;
            _stroke.StrokeWidth = radius * 0.14f;
            var r = radius * 0.55f;
            canvas.DrawArc(cx - r, cy - r, cx + r, cy + r, _spin, 270, false, _stroke);
            _spin = (_spin + 12) % 360;
            PostInvalidateDelayed(30);
        }
        else if (Look == BubbleLook.Error)
        {
            _text.Color = Color.White;
            _text.TextSize = radius * 1.1f;
            canvas.DrawText("!", cx, cy + _text.TextSize * 0.36f, _text);
        }
        else
        {
            DrawMicrophone(canvas, cx, cy, radius);
        }

        if (English && Look != BubbleLook.Processing)
        {
            _fill.Color = Color.White;
            var badge = radius * 0.42f;
            var bx = cx + radius * 0.62f;
            var by = cy + radius * 0.62f;
            canvas.DrawCircle(bx, by, badge, _fill);
            _text.Color = EnglishColor;
            _text.TextSize = badge * 0.95f;
            canvas.DrawText("EN", bx, by + _text.TextSize * 0.35f, _text);
        }
    }

    private void DrawMicrophone(Canvas canvas, float cx, float cy, float radius)
    {
        _stroke.Color = Color.White;
        _fill.Color = Color.White;
        _stroke.StrokeWidth = radius * 0.1f;

        var headW = radius * 0.34f;
        var headTop = cy - radius * 0.5f;
        var headBottom = cy + radius * 0.12f;
        canvas.DrawRoundRect(cx - headW / 2, headTop, cx + headW / 2, headBottom, headW / 2, headW / 2, _fill);

        var cup = radius * 0.34f;
        canvas.DrawArc(cx - cup, headBottom - cup * 1.25f, cx + cup, headBottom + cup * 0.55f, 0, 180, false, _stroke);
        var stemTop = headBottom + cup * 0.55f;
        canvas.DrawLine(cx, stemTop, cx, stemTop + radius * 0.2f, _stroke);
    }
}

/// <summary>
/// The recording cue between ✓ and ✕: a dark capsule with bars that rise and fall with the voice, like the
/// Windows pill. Blue while the dictation will be translated to English.
/// </summary>
public sealed class LevelBars : View
{
    private static readonly float[] Shape = [0.45f, 0.75f, 1f, 0.75f, 0.45f];

    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _bar = new(PaintFlags.AntiAlias) { Color = Color.White };
    private readonly Random _jitter = new();
    private readonly float[] _heights = new float[Shape.Length];
    private float _level;

    public LevelBars(Context context) : base(context)
    {
        ContentDescription = "Listening";
    }

    public bool English { get; set; }

    public void Reset()
    {
        _level = 0;
        Array.Clear(_heights);
        Invalidate();
    }

    /// <summary>0..1, from the controller's level meter.</summary>
    public void SetLevel(float level)
    {
        // Fast attack, slow release, so the bars read as speech rather than flicker.
        _level = level > _level ? level : _level * 0.8f + level * 0.2f;
        var loud = Math.Clamp(_level * 1.8f, 0f, 1f);
        for (var i = 0; i < _heights.Length; i++)
        {
            _heights[i] = Shape[i] * loud * (0.75f + 0.25f * (float)_jitter.NextDouble());
        }

        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        var h = Height;
        _fill.Color = English ? Color.ParseColor("#1565C0") : Color.ParseColor("#263238");
        canvas.DrawRoundRect(0, 0, Width, h, h / 2f, h / 2f, _fill);

        var barWidth = h * 0.1f;
        var gap = barWidth * 0.9f;
        var total = Shape.Length * barWidth + (Shape.Length - 1) * gap;
        var x = (Width - total) / 2f;
        var cy = h / 2f;
        var max = h * 0.62f;
        for (var i = 0; i < _heights.Length; i++)
        {
            var half = Math.Max(barWidth, max * _heights[i]) / 2f;
            canvas.DrawRoundRect(x, cy - half, x + barWidth, cy + half, barWidth / 2f, barWidth / 2f, _bar);
            x += barWidth + gap;
        }
    }
}

/// <summary>A small round button with a check or a cross.</summary>
public sealed class GlyphButton : View
{
    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeCap = Paint.Cap.Round };
    private readonly bool _check;

    public GlyphButton(Context context, bool check) : base(context)
    {
        _check = check;
        _stroke.SetStyle(Paint.Style.Stroke);
        ContentDescription = check ? "Insert" : "Cancel";
    }

    protected override void OnDraw(Canvas canvas)
    {
        var cx = Width / 2f;
        var cy = Height / 2f;
        var radius = Math.Min(Width, Height) / 2f * 0.86f;
        _fill.Color = _check ? Color.ParseColor("#2E7D32") : Color.ParseColor("#616161");
        canvas.DrawCircle(cx, cy, radius, _fill);

        _stroke.Color = Color.White;
        _stroke.StrokeWidth = radius * 0.16f;
        var s = radius * 0.42f;
        if (_check)
        {
            var path = new Android.Graphics.Path();
            path.MoveTo(cx - s, cy);
            path.LineTo(cx - s * 0.25f, cy + s * 0.7f);
            path.LineTo(cx + s, cy - s * 0.6f);
            canvas.DrawPath(path, _stroke);
        }
        else
        {
            canvas.DrawLine(cx - s, cy - s, cx + s, cy + s, _stroke);
            canvas.DrawLine(cx - s, cy + s, cx + s, cy - s, _stroke);
        }
    }
}
