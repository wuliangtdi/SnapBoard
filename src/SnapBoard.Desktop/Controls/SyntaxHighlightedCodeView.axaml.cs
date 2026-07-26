using System.Collections.Frozen;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SnapBoard.Desktop.Controls;

/// <summary>
/// 面向剪贴板只读预览的轻量语法着色控件。
/// 它只做一次线性扫描，不承担编译器级语法分析，避免为预览引入 Roslyn 或编辑器运行时。
/// 完整的多语言高亮会在 Phase 1.5 通过可测量、可裁剪的独立服务实现。
/// </summary>
public partial class SyntaxHighlightedCodeView : UserControl
{
    public static readonly StyledProperty<string?> CodeProperty =
        AvaloniaProperty.Register<SyntaxHighlightedCodeView, string?>(nameof(Code));

    private static readonly FrozenSet<string> Keywords = new[]
    {
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
        "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed",
        "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal",
        "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "partial", "private", "protected", "public", "readonly", "record", "ref", "return",
        "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "var", "virtual", "void", "volatile", "while", "with", "yield",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> BuiltInTypes = new[]
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte",
        "short", "string", "uint", "ulong", "ushort", "void",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IBrush DefaultBrush = Brush.Parse("#172033");
    private static readonly IBrush KeywordBrush = Brush.Parse("#7C3AED");
    private static readonly IBrush TypeBrush = Brush.Parse("#007A78");
    private static readonly IBrush NamespaceBrush = Brush.Parse("#1677FF");
    private static readonly IBrush LiteralBrush = Brush.Parse("#B7791F");
    private static readonly IBrush CommentBrush = Brush.Parse("#2F855A");

    public SyntaxHighlightedCodeView()
    {
        InitializeComponent();
        RebuildLines(Code ?? string.Empty);
    }

    public ObservableCollection<CodePreviewLine> Lines { get; } = [];

    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == CodeProperty)
        {
            RebuildLines(change.GetNewValue<string?>() ?? string.Empty);
        }
    }

    private void RebuildLines(string code)
    {
        Lines.Clear();
        string normalizedCode = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] sourceLines = normalizedCode.Split('\n');

        for (int index = 0; index < sourceLines.Length; index++)
        {
            Lines.Add(new CodePreviewLine(index + 1, Tokenize(sourceLines[index])));
        }
    }

    private static List<CodePreviewToken> Tokenize(string line)
    {
        List<CodePreviewToken> tokens = [];
        bool expectNamespace = false;

        for (int index = 0; index < line.Length;)
        {
            char current = line[index];

            if (char.IsWhiteSpace(current))
            {
                int start = index++;
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                {
                    index++;
                }

                tokens.Add(new CodePreviewToken(line[start..index], DefaultBrush));
                continue;
            }

            if (current == '/' && index + 1 < line.Length && line[index + 1] == '/')
            {
                tokens.Add(new CodePreviewToken(line[index..], CommentBrush));
                break;
            }

            if (current is '\"' or '\'')
            {
                int start = index++;
                bool escaped = false;
                while (index < line.Length)
                {
                    char value = line[index++];
                    if (value == current && !escaped)
                    {
                        break;
                    }

                    escaped = value == '\\' && !escaped;
                    if (value != '\\')
                    {
                        escaped = false;
                    }
                }

                tokens.Add(new CodePreviewToken(line[start..index], LiteralBrush));
                continue;
            }

            if (current == '[')
            {
                int start = index++;
                while (index < line.Length && line[index] != ']')
                {
                    index++;
                }

                if (index < line.Length)
                {
                    index++;
                }

                tokens.Add(new CodePreviewToken(line[start..index], TypeBrush));
                continue;
            }

            if (char.IsDigit(current))
            {
                int start = index++;
                while (index < line.Length && (char.IsDigit(line[index]) || line[index] == '.'))
                {
                    index++;
                }

                tokens.Add(new CodePreviewToken(line[start..index], LiteralBrush));
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                int start = index++;
                while (index < line.Length &&
                       (char.IsLetterOrDigit(line[index]) || line[index] is '_' or '.' or '?'))
                {
                    index++;
                }

                string token = line[start..index];
                tokens.Add(new CodePreviewToken(token, GetIdentifierBrush(token, expectNamespace)));
                expectNamespace = token == "using";
                continue;
            }

            tokens.Add(new CodePreviewToken(current.ToString(), DefaultBrush));
            index++;
        }

        return tokens;
    }

    private static IBrush GetIdentifierBrush(string token, bool expectNamespace)
    {
        if (BuiltInTypes.Contains(token))
        {
            return TypeBrush;
        }

        if (Keywords.Contains(token))
        {
            return KeywordBrush;
        }

        if (expectNamespace || token.Contains('.', StringComparison.Ordinal))
        {
            return NamespaceBrush;
        }

        return char.IsUpper(token[0]) ? TypeBrush : DefaultBrush;
    }
}

public sealed record CodePreviewLine(int Number, IReadOnlyList<CodePreviewToken> Tokens);

public sealed record CodePreviewToken(string Text, IBrush Foreground);
