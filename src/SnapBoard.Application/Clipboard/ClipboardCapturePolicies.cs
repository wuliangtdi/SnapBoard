using System.Text;
using SnapBoard.Domain.Clipboard;
using SnapBoard.Platform.Abstractions.Clipboard;

namespace SnapBoard.Application.Clipboard;

public enum ClipboardApplicationRuleMode
{
    Capture = 0,
    Ignore = 1,
    TextOnly = 2,
}

public sealed class ClipboardCaptureOptions
{
    private static readonly IReadOnlySet<string> DefaultPasswordManagers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "1password",
            "bitwarden",
            "dashlane",
            "enpass",
            "keepass",
            "keepassxc",
            "lastpass",
            "roboform",
        };

    private static readonly IReadOnlySet<string> DefaultSensitiveFormatTokens =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "com.agilebits.onepassword",
            "org.keepassxc",
            "bitwarden",
            "dashlane",
            "enpass",
            "excludeclipboardcontentfrommonitorprocessing",
            "keepass",
            "lastpass",
            "org.nspasteboard.transienttype",
            "org.nspasteboard.concealedtype",
            "passwordmanager",
            "roboform",
        };

    public long MaximumPayloadBytes { get; init; } = 64L * 1024 * 1024;

    public int MaximumSearchableCharacters { get; init; } = 1_000_000;

    public IReadOnlySet<ClipboardContentKind> EnabledContentKinds { get; init; } =
        new HashSet<ClipboardContentKind>(Enum.GetValues<ClipboardContentKind>());

    public IReadOnlyDictionary<string, ClipboardApplicationRuleMode> ApplicationRules { get; init; } =
        new Dictionary<string, ClipboardApplicationRuleMode>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> PasswordManagerProcessNames { get; init; } = DefaultPasswordManagers;

    public IReadOnlySet<string> SensitiveFormatTokens { get; init; } = DefaultSensitiveFormatTokens;
}

public sealed record ClipboardCapturePolicyContext(ClipboardContentSnapshot Snapshot);

public enum ClipboardCapturePolicyAction
{
    Continue = 0,
    Ignore = 1,
    TextOnly = 2,
}

public sealed record ClipboardCapturePolicyResult(
    ClipboardCapturePolicyAction Action,
    string ReasonCode)
{
    public static ClipboardCapturePolicyResult Continue { get; } = new(
        ClipboardCapturePolicyAction.Continue,
        "continue");
}

public sealed record ClipboardCapturePolicyDecision(
    bool ShouldCapture,
    bool TextOnly,
    string ReasonCode);

public interface IClipboardCapturePolicy
{
    ValueTask<ClipboardCapturePolicyResult> EvaluateAsync(
        ClipboardCapturePolicyContext context,
        CancellationToken cancellationToken);
}

public interface IClipboardCapturePolicyChain
{
    ValueTask<ClipboardCapturePolicyDecision> EvaluateAsync(
        ClipboardContentSnapshot snapshot,
        CancellationToken cancellationToken);
}

public sealed class ClipboardCapturePolicyChain(
    IEnumerable<IClipboardCapturePolicy> policies) : IClipboardCapturePolicyChain
{
    private readonly IReadOnlyList<IClipboardCapturePolicy> _policies = policies.ToArray();

    public async ValueTask<ClipboardCapturePolicyDecision> EvaluateAsync(
        ClipboardContentSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ClipboardCapturePolicyContext context = new(snapshot);
        bool textOnly = false;

        foreach (IClipboardCapturePolicy policy in _policies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClipboardCapturePolicyResult result = await policy
                .EvaluateAsync(context, cancellationToken)
                .ConfigureAwait(false);
            if (result.Action == ClipboardCapturePolicyAction.Ignore)
            {
                return new ClipboardCapturePolicyDecision(false, textOnly, result.ReasonCode);
            }

            textOnly |= result.Action == ClipboardCapturePolicyAction.TextOnly;
        }

        return new ClipboardCapturePolicyDecision(true, textOnly, "capture");
    }
}

public sealed class CurrentApplicationClipboardPolicy : IClipboardCapturePolicy
{
    public ValueTask<ClipboardCapturePolicyResult> EvaluateAsync(
        ClipboardCapturePolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(context.Snapshot.IsFromCurrentApplication
            ? new ClipboardCapturePolicyResult(
                ClipboardCapturePolicyAction.Ignore,
                "current-application")
            : ClipboardCapturePolicyResult.Continue);
    }
}

public sealed class SensitiveClipboardPolicy(
    ClipboardCaptureOptions options) : IClipboardCapturePolicy
{
    public ValueTask<ClipboardCapturePolicyResult> EvaluateAsync(
        ClipboardCapturePolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string processName = NormalizeProcessName(context.Snapshot.Source.ProcessName);
        if (processName.Length > 0 && options.PasswordManagerProcessNames.Contains(processName))
        {
            return ValueTask.FromResult(new ClipboardCapturePolicyResult(
                ClipboardCapturePolicyAction.Ignore,
                "password-manager"));
        }

        foreach (ClipboardFormatDescriptor format in context.Snapshot.Formats)
        {
            if (options.SensitiveFormatTokens.Any(token =>
                format.Identifier.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                format.Name.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return ValueTask.FromResult(new ClipboardCapturePolicyResult(
                    ClipboardCapturePolicyAction.Ignore,
                    "sensitive-format"));
            }
        }

        return ValueTask.FromResult(ClipboardCapturePolicyResult.Continue);
    }

    internal static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        return Path.GetFileNameWithoutExtension(processName.Trim());
    }
}

public sealed class ApplicationRuleClipboardPolicy(
    ClipboardCaptureOptions options) : IClipboardCapturePolicy
{
    public ValueTask<ClipboardCapturePolicyResult> EvaluateAsync(
        ClipboardCapturePolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string processName = SensitiveClipboardPolicy.NormalizeProcessName(
            context.Snapshot.Source.ProcessName);
        string executableName = SensitiveClipboardPolicy.NormalizeProcessName(
            context.Snapshot.Source.ExecutablePath);
        ClipboardApplicationRuleMode mode = FindMode(processName, executableName);
        return ValueTask.FromResult(mode switch
        {
            ClipboardApplicationRuleMode.Ignore => new ClipboardCapturePolicyResult(
                ClipboardCapturePolicyAction.Ignore,
                "application-blacklist"),
            ClipboardApplicationRuleMode.TextOnly => new ClipboardCapturePolicyResult(
                ClipboardCapturePolicyAction.TextOnly,
                "application-text-only"),
            _ => ClipboardCapturePolicyResult.Continue,
        });
    }

    private ClipboardApplicationRuleMode FindMode(string processName, string executableName)
    {
        if (processName.Length > 0 && options.ApplicationRules.TryGetValue(processName, out var mode))
        {
            return mode;
        }

        return executableName.Length > 0 &&
            options.ApplicationRules.TryGetValue(executableName, out mode)
            ? mode
            : ClipboardApplicationRuleMode.Capture;
    }
}

public sealed class PayloadSizeClipboardPolicy(
    ClipboardCaptureOptions options) : IClipboardCapturePolicy
{
    public ValueTask<ClipboardCapturePolicyResult> EvaluateAsync(
        ClipboardCapturePolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long size = CalculatePayloadSize(context.Snapshot, options.MaximumPayloadBytes);
        return ValueTask.FromResult(size > options.MaximumPayloadBytes
            ? new ClipboardCapturePolicyResult(
                ClipboardCapturePolicyAction.Ignore,
                "payload-too-large")
            : ClipboardCapturePolicyResult.Continue);
    }

    private static long CalculatePayloadSize(
        ClipboardContentSnapshot snapshot,
        long stopAfter)
    {
        long total = AddSaturated(0, snapshot.Html.Length);
        total = AddSaturated(total, snapshot.RichText.Length);
        total = AddSaturated(total, snapshot.Bitmap?.Data.Length ?? 0);
        if (snapshot.Text is not null)
        {
            total = AddSaturated(total, Encoding.UTF8.GetByteCount(snapshot.Text));
        }

        foreach (string path in snapshot.FilePaths)
        {
            total = AddSaturated(total, Encoding.UTF8.GetByteCount(path));
            if (total > stopAfter)
            {
                break;
            }
        }

        return total;
    }

    private static long AddSaturated(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed class SupportedContentClipboardPolicy(
    ClipboardCaptureOptions options) : IClipboardCapturePolicy
{
    public ValueTask<ClipboardCapturePolicyResult> EvaluateAsync(
        ClipboardCapturePolicyContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ClipboardContentSnapshot snapshot = context.Snapshot;
        bool hasSupportedContent =
            (options.EnabledContentKinds.Contains(ClipboardContentKind.Text) && snapshot.Text is not null) ||
            (options.EnabledContentKinds.Contains(ClipboardContentKind.Html) && !snapshot.Html.IsEmpty) ||
            (options.EnabledContentKinds.Contains(ClipboardContentKind.RichText) && !snapshot.RichText.IsEmpty) ||
            (options.EnabledContentKinds.Contains(ClipboardContentKind.Image) && snapshot.Bitmap is not null) ||
            (options.EnabledContentKinds.Contains(ClipboardContentKind.FileReference) && snapshot.FilePaths.Count > 0);

        return ValueTask.FromResult(hasSupportedContent
            ? ClipboardCapturePolicyResult.Continue
            : new ClipboardCapturePolicyResult(
                ClipboardCapturePolicyAction.Ignore,
                "no-supported-content"));
    }
}
