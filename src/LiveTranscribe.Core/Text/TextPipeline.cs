using System.Text.RegularExpressions;

namespace LiveTranscribe.Core.Text;

/// <summary>One deterministic step applied to engine output before insertion.</summary>
public interface ITextStage
{
    string Apply(string text);
}

/// <summary>
/// Ordered text stages. M1 shipped line-break removal and whitespace tidying, M3 added the control
/// character filter; the Persian normalizer and the refinement slot join in M4 (architecture §4.6).
/// </summary>
public sealed class TextPipeline
{
    private readonly IReadOnlyList<ITextStage> _stages;

    public TextPipeline(IEnumerable<ITextStage> stages)
    {
        _stages = stages.ToList();
    }

    public static TextPipeline CreateDefault(bool allowLineBreaks) => new(new ITextStage[]
    {
        new LineBreakPolicy(allowLineBreaks),
        new ControlCharacterFilter(allowLineBreaks),
        new WhitespaceTidy()
    });

    public string Process(string text) => _stages.Aggregate(text, (current, stage) => stage.Apply(current));
}

/// <summary>A newline can send the message in chat apps, so line breaks and bullets become spaces (D8).</summary>
public sealed partial class LineBreakPolicy : ITextStage
{
    private readonly bool _allowLineBreaks;

    public LineBreakPolicy(bool allowLineBreaks)
    {
        _allowLineBreaks = allowLineBreaks;
    }

    public string Apply(string text)
    {
        if (_allowLineBreaks)
        {
            return text;
        }

        var withoutBullets = BulletPrefix().Replace(text, " ");
        return LineBreaks().Replace(withoutBullets, " ");
    }

    [GeneratedRegex(@"(^|\r?\n)\s*([-*•]|\d+[.)])\s+")]
    private static partial Regex BulletPrefix();

    [GeneratedRegex(@"[\r\n  ]+")]
    private static partial Regex LineBreaks();
}

/// <summary>
/// Drops characters that would act on the target instead of appearing in it: C0 and C1 controls, and
/// the bidi overrides that silently reorder everything around them (FR-1.6, FR-5.14, D8). ZWNJ is
/// kept because Persian needs it (FR-5.7), and line breaks survive only when the user allowed them.
/// </summary>
public sealed class ControlCharacterFilter : ITextStage
{
    private const char Zwnj = '‌';

    private readonly bool _allowLineBreaks;

    public ControlCharacterFilter(bool allowLineBreaks)
    {
        _allowLineBreaks = allowLineBreaks;
    }

    public string Apply(string text) =>
        text.Any(IsUnsafe) ? string.Concat(text.Where(c => !IsUnsafe(c))) : text;

    /// <summary>
    /// What insertion must never send whatever the settings say, so the guard in the inserter does
    /// not have to know about the line-break policy (FR-1.6).
    /// </summary>
    public static bool IsUnsafeForInsertion(char c) => c is not ('\r' or '\n') && IsControlOrBidi(c);

    private bool IsUnsafe(char c) =>
        c is '\r' or '\n' ? !_allowLineBreaks : IsControlOrBidi(c);

    private static bool IsControlOrBidi(char c)
    {
        if (c == Zwnj || c == ' ')
        {
            return false;
        }

        // A tab moves to the next control in dialogs and web forms, so it is never inserted (FR-1.6).
        return c <= ''
               || c is >= '' and <= ''
               || c is '‎' or '‏'
               || c is >= '‪' and <= '‮'
               || c is >= '⁦' and <= '⁩';
    }
}

public sealed partial class WhitespaceTidy : ITextStage
{
    public string Apply(string text) => MultipleSpaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultipleSpaces();
}
