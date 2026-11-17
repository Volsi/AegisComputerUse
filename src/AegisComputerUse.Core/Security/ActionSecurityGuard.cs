using System.Text.Json;
using System.Text.RegularExpressions;
using AegisComputerUse.Core.Native;

namespace AegisComputerUse.Core.Security;

public enum ActionVerdict
{
    Allow,
    RequireConfirmation,
    Block
}

public sealed record ActionPolicyDecision(
    ActionVerdict Verdict,
    string Reason,
    string ActionSummary);

public sealed class ActionSecurityPolicyOptions
{
    public int MaxStepsPerRun { get; set; } = 25;
    public int MinActionIntervalMs { get; set; } = 250;
    public bool EnforceTargetWindowBounds { get; set; }
    public Win32Interop.RECT? TargetWindowRect { get; set; }
    public bool BlockSensitiveForegroundWindows { get; set; } = true;
    public bool RequireConfirmationOnTypingCommands { get; set; } = true;
    public bool AllowBrowserNavigation { get; set; } = true;
}

public sealed partial class ActionSecurityGuard
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https"
    };

    private static readonly string[] SensitiveWindowKeywords =
    [
        "1password", "keepass", "bitwarden", "lastpass", "dashlane", "roboform",
        "windows security", "безопасность windows", "user account control",
        "контроль учетных записей", "credential", "учетные данные",
        "authenticator", "password", "пароль", "passphrase", "seed phrase"
    ];

    private static readonly string[] DangerousHotkeys =
    [
        "ctrl+alt+del", "ctrl+alt+delete", "control+alt+delete",
        "win+l", "super+l", "meta+l"
    ];

    private static readonly string[] ConfirmationHotkeys =
    [
        "win+r", "super+r", "meta+r",
        "win+x", "super+x", "meta+x",
        "ctrl+shift+esc", "control+shift+escape",
        "alt+f4", "win+i", "win"
    ];

    [GeneratedRegex(@"(?i)\b(powershell|pwsh|cmd\.exe|cmd\s+/c|mshta|wscript|cscript|regedit|certutil|bitsadmin|schtasks|rundll32|regsvr32|invoke-expression|iex\s+|net\s+user|net\s+localgroup|netsh|bcdedit|diskpart|vssadmin|wevtutil|takeown|icacls|sc\.exe|taskkill|system32|syswow64|format\s+[a-z]:|del\s+/[sqf]|rd\s+/s|rm\s+-rf|curl\s+.*\|)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DangerousCommandTextRegex();

    private readonly ActionSecurityPolicyOptions _options;

    public ActionSecurityGuard(ActionSecurityPolicyOptions? options = null)
    {
        _options = options ?? new ActionSecurityPolicyOptions();
    }

    public bool IsSensitiveWindowTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        foreach (string kw in SensitiveWindowKeywords)
        {
            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public ActionPolicyDecision EvaluateScreenCapture(string? foregroundWindowTitle)
    {
        if (_options.BlockSensitiveForegroundWindows && IsSensitiveWindowTitle(foregroundWindowTitle))
        {
            return new ActionPolicyDecision(
                ActionVerdict.RequireConfirmation,
                $"Sensitive window detected in foreground ('{foregroundWindowTitle}'). Confirm capture or switch window.",
                "screen_capture");
        }

        return new ActionPolicyDecision(ActionVerdict.Allow, "Screen capture permitted.", "screen_capture");
    }

    public ActionPolicyDecision EvaluateAction(
        string functionName,
        JsonElement args,
        int currentStepNumber,
        int screenWidth,
        int screenHeight,
        int screenOriginX = 0,
        int screenOriginY = 0,
        string? foregroundWindowTitle = null)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            return new ActionPolicyDecision(ActionVerdict.Block, "Empty action name.", "unknown");

        int maxSteps = Math.Clamp(_options.MaxStepsPerRun, 1, 100);
        if (currentStepNumber > maxSteps)
            return new ActionPolicyDecision(ActionVerdict.Block, $"Step budget exceeded ({maxSteps}).", functionName);

        if (_options.BlockSensitiveForegroundWindows && IsSensitiveWindowTitle(foregroundWindowTitle))
        {
            return new ActionPolicyDecision(
                ActionVerdict.RequireConfirmation,
                $"Sensitive window active ('{foregroundWindowTitle}'). Manual confirmation required.",
                functionName);
        }

        // Check Gemini built-in safety_decision first (never auto-approve!)
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("safety_decision", out JsonElement safetyEl) &&
            safetyEl.ValueKind == JsonValueKind.Object)
        {
            string? decision = safetyEl.TryGetProperty("decision", out var dEl) ? dEl.GetString() : null;
            string? explanation = safetyEl.TryGetProperty("explanation", out var eEl) ? eEl.GetString() : null;
            if (!string.Equals(decision, "allowed", StringComparison.OrdinalIgnoreCase))
            {
                return new ActionPolicyDecision(
                    ActionVerdict.RequireConfirmation,
                    string.IsNullOrWhiteSpace(explanation)
                        ? "Model safety_decision requires user confirmation."
                        : explanation,
                    functionName);
            }
        }

        switch (functionName)
        {
            case "click":
            case "click_at":
            case "left_click":
            case "left_click_at":
            case "double_click":
            case "double_click_at":
            case "double_click_coordinate":
            case "right_click":
            case "right_click_at":
            case "triple_click":
            case "triple_click_at":
            case "middle_click":
            case "middle_click_at":
            case "hover":
            case "hover_at":
            case "move":
            case "move_cursor":
            case "move_to":
            case "mouse_move":
            case "mouse_down":
            case "mouse_up":
            {
                if (TryExtractPoint(args, "x", "y", out int nx, out int ny))
                {
                    if (nx < 0 || nx > 1000 || ny < 0 || ny > 1000)
                        return new ActionPolicyDecision(ActionVerdict.Block, $"Coordinates ({nx}, {ny}) out of valid normalized range [0..1000].", functionName);

                    var (px, py) = MapToScreen(nx, ny, screenWidth, screenHeight, screenOriginX, screenOriginY);
                    if (!CheckTargetWindowBounds(px, py, out string? boundsErr))
                        return new ActionPolicyDecision(ActionVerdict.Block, boundsErr!, $"{functionName}({nx},{ny})");

                    return new ActionPolicyDecision(ActionVerdict.Allow, "OK", $"{functionName}({nx},{ny})");
                }

                if (Win32Interop.GetCursorPos(out var cur))
                {
                    if (!CheckTargetWindowBounds(cur.X, cur.Y, out string? boundsErr))
                        return new ActionPolicyDecision(ActionVerdict.Block, boundsErr!, functionName);
                }

                return new ActionPolicyDecision(ActionVerdict.Allow, "OK", functionName);
            }

            case "drag":
            case "drag_and_drop":
            case "drag_and_drop_at":
            {
                string? err1 = null;
                string? err2 = null;
                if (!TryReadNormalizedPoint(args, "x", "y", out int sx, out int sy, out err1) ||
                    !TryReadNormalizedPoint(args, "destination_x", "destination_y", out int dx, out int dy, out err2))
                    return new ActionPolicyDecision(ActionVerdict.Block, err1 ?? err2!, functionName);

                var (psx, psy) = MapToScreen(sx, sy, screenWidth, screenHeight, screenOriginX, screenOriginY);
                var (pdx, pdy) = MapToScreen(dx, dy, screenWidth, screenHeight, screenOriginX, screenOriginY);
                if (!CheckTargetWindowBounds(psx, psy, out string? b1) ||
                    !CheckTargetWindowBounds(pdx, pdy, out b1))
                    return new ActionPolicyDecision(ActionVerdict.Block, b1!, $"{functionName}({sx},{sy}->{dx},{dy})");

                return new ActionPolicyDecision(ActionVerdict.Allow, "OK", $"{functionName}({sx},{sy}->{dx},{dy})");
            }

            case "scroll":
            case "scroll_at":
            {
                if (TryExtractPoint(args, "x", "y", out int nx, out int ny))
                {
                    var (px, py) = MapToScreen(nx, ny, screenWidth, screenHeight, screenOriginX, screenOriginY);
                    if (!CheckTargetWindowBounds(px, py, out string? boundsErr))
                        return new ActionPolicyDecision(ActionVerdict.Block, boundsErr!, $"{functionName}({nx},{ny})");

                    return new ActionPolicyDecision(ActionVerdict.Allow, "OK", $"{functionName}({nx},{ny})");
                }

                return new ActionPolicyDecision(ActionVerdict.Allow, "OK", functionName);
            }

            case "scroll_document":
            case "wait":
            case "wait_5_seconds":
            case "sleep":
            case "go_back":
            case "go_forward":
                return new ActionPolicyDecision(ActionVerdict.Allow, "OK", functionName);

            case "type":
            case "type_text":
            case "type_text_at":
            case "write":
            {
                if (TryExtractPoint(args, "x", "y", out int nx, out int ny))
                {
                    var (px, py) = MapToScreen(nx, ny, screenWidth, screenHeight, screenOriginX, screenOriginY);
                    if (!CheckTargetWindowBounds(px, py, out string? boundsErr))
                        return new ActionPolicyDecision(ActionVerdict.Block, boundsErr!, $"{functionName}({nx},{ny})");
                }

                string text = args.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? string.Empty : string.Empty;
                if (text.Length > 8192)
                    return new ActionPolicyDecision(ActionVerdict.Block, "Typed text length exceeds 8192 characters.", functionName);

                if (_options.RequireConfirmationOnTypingCommands && DangerousCommandTextRegex().IsMatch(text))
                {
                    return new ActionPolicyDecision(
                        ActionVerdict.RequireConfirmation,
                        "Potentially dangerous command/shell text detected in type. Explicit user approval required.",
                        $"{functionName}(len={text.Length})");
                }

                return new ActionPolicyDecision(ActionVerdict.Allow, "OK", $"{functionName}(len={text.Length})");
            }

            case "key_combination":
            case "press_key":
            case "key_press":
            case "press_keys":
            case "hotkey":
            {
                string keys = ReadKeysString(args);
                if (string.IsNullOrWhiteSpace(keys))
                    return new ActionPolicyDecision(ActionVerdict.Block, "Empty key combination.", functionName);

                string normalized = NormalizeCombo(keys);
                if (DangerousHotkeys.Contains(normalized))
                {
                    return new ActionPolicyDecision(
                        ActionVerdict.Block,
                        $"Blocked critical OS hotkey: {keys}",
                        $"{functionName}({keys})");
                }

                if (ConfirmationHotkeys.Contains(normalized))
                {
                    return new ActionPolicyDecision(
                        ActionVerdict.RequireConfirmation,
                        $"System shortcut '{keys}' requires explicit user confirmation.",
                        $"{functionName}({keys})");
                }

                return new ActionPolicyDecision(ActionVerdict.Allow, "OK", $"{functionName}({keys})");
            }

            case "navigate":
            {
                if (!_options.AllowBrowserNavigation)
                    return new ActionPolicyDecision(ActionVerdict.Block, "Browser navigation is disabled by policy.", functionName);

                string url = args.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? string.Empty : string.Empty;
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || !AllowedSchemes.Contains(parsed.Scheme))
                {
                    return new ActionPolicyDecision(
                        ActionVerdict.Block,
                        $"Blocked non-HTTP(S) URL scheme in navigate: '{LogRedactor.Redact(url)}'",
                        functionName);
                }

                return new ActionPolicyDecision(ActionVerdict.Allow, "OK", $"{functionName}({parsed.Host})");
            }

            case "open_web_browser":
            case "search":
                return _options.AllowBrowserNavigation
                    ? new ActionPolicyDecision(ActionVerdict.Allow, "OK", functionName)
                    : new ActionPolicyDecision(ActionVerdict.Block, "Browser launch disabled by policy.", functionName);

            case "create_file":
                return new ActionPolicyDecision(ActionVerdict.Allow, "Validated by SafeFileSandbox.", functionName);

            default:
                return new ActionPolicyDecision(ActionVerdict.Block, $"Unknown/unapproved tool action '{functionName}'.", functionName);
        }
    }

    public static (int X, int Y) MapToScreen(int normX, int normY, int screenWidth, int screenHeight, int originX = 0, int originY = 0)
    {
        int clampedX = Math.Clamp(normX, 0, 1000);
        int clampedY = Math.Clamp(normY, 0, 1000);
        int px = originX + (int)Math.Round(clampedX / 1000.0 * Math.Max(1, screenWidth - 1));
        int py = originY + (int)Math.Round(clampedY / 1000.0 * Math.Max(1, screenHeight - 1));
        return (px, py);
    }

    private bool CheckTargetWindowBounds(int px, int py, out string? error)
    {
        if (!_options.EnforceTargetWindowBounds || _options.TargetWindowRect is null)
        {
            error = null;
            return true;
        }

        var r = _options.TargetWindowRect.Value;
        if (px < r.Left || px > r.Right || py < r.Top || py > r.Bottom)
        {
            error = $"Action coordinates ({px},{py}) fall outside locked target window bounds [{r.Left},{r.Top}..{r.Right},{r.Bottom}].";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryExtractPoint(JsonElement args, string xProp, string yProp, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (args.ValueKind != JsonValueKind.Object) return false;

        // 1. Direct properties (e.g. "x", "y")
        if (args.TryGetProperty(xProp, out var xEl) && args.TryGetProperty(yProp, out var yEl) &&
            TryGetFiniteDouble(xEl, out double dx) && TryGetFiniteDouble(yEl, out double dy))
        {
            x = (int)Math.Round(dx);
            y = (int)Math.Round(dy);
            return true;
        }

        // 2. Check "point" or "coordinate" or "coordinates" or "position" array [x, y]
        string[] arrayProps = ["point", "coordinate", "coordinates", "position", "coords"];
        foreach (string prop in arrayProps)
        {
            if (args.TryGetProperty(prop, out var arrEl))
            {
                if (arrEl.ValueKind == JsonValueKind.Array && arrEl.GetArrayLength() >= 2)
                {
                    if (TryGetFiniteDouble(arrEl[0], out double ax) && TryGetFiniteDouble(arrEl[1], out double ay))
                    {
                        x = (int)Math.Round(ax);
                        y = (int)Math.Round(ay);
                        return true;
                    }
                }
                else if (arrEl.ValueKind == JsonValueKind.Object)
                {
                    if (arrEl.TryGetProperty(xProp, out var oxEl) && arrEl.TryGetProperty(yProp, out var oyEl) &&
                        TryGetFiniteDouble(oxEl, out double ox) && TryGetFiniteDouble(oyEl, out double oy))
                    {
                        x = (int)Math.Round(ox);
                        y = (int)Math.Round(oy);
                        return true;
                    }
                }
            }
        }

        // 3. Check destination / end coordinates for drag
        if (xProp.Contains("destination", StringComparison.OrdinalIgnoreCase) || xProp.Contains("end", StringComparison.OrdinalIgnoreCase))
        {
            string[] destProps = ["destination_point", "end_point", "destination", "target", "to"];
            foreach (string prop in destProps)
            {
                if (args.TryGetProperty(prop, out var arrEl))
                {
                    if (arrEl.ValueKind == JsonValueKind.Array && arrEl.GetArrayLength() >= 2)
                    {
                        if (TryGetFiniteDouble(arrEl[0], out double ax) && TryGetFiniteDouble(arrEl[1], out double ay))
                        {
                            x = (int)Math.Round(ax);
                            y = (int)Math.Round(ay);
                            return true;
                        }
                    }
                    else if (arrEl.ValueKind == JsonValueKind.Object)
                    {
                        if (arrEl.TryGetProperty("x", out var oxEl) && arrEl.TryGetProperty("y", out var oyEl) &&
                            TryGetFiniteDouble(oxEl, out double ox) && TryGetFiniteDouble(oyEl, out double oy))
                        {
                            x = (int)Math.Round(ox);
                            y = (int)Math.Round(oy);
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private static bool TryReadNormalizedPoint(JsonElement args, string xProp, string yProp, out int x, out int y, out string? error)
    {
        x = 0;
        y = 0;
        if (!TryExtractPoint(args, xProp, yProp, out x, out y))
        {
            error = $"Missing or invalid coordinates ({xProp}, {yProp}).";
            return false;
        }

        if (x < 0 || x > 1000 || y < 0 || y > 1000)
        {
            error = $"Coordinates ({x}, {y}) out of valid normalized range [0..1000].";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryGetFiniteDouble(JsonElement el, out double val)
    {
        val = 0;
        if (el.ValueKind != JsonValueKind.Number) return false;
        if (!el.TryGetDouble(out double d)) return false;
        if (double.IsNaN(d) || double.IsInfinity(d)) return false;
        val = d;
        return true;
    }

    private static string ReadKeysString(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return string.Empty;
        if (args.TryGetProperty("keys", out var kEl))
        {
            if (kEl.ValueKind == JsonValueKind.String) return kEl.GetString() ?? string.Empty;
            if (kEl.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in kEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        parts.Add(item.GetString()!);
                }
                return string.Join("+", parts);
            }
        }
        return args.TryGetProperty("key", out var single) && single.ValueKind == JsonValueKind.String
            ? single.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string NormalizeCombo(string keys)
    {
        var tokens = keys.Split(['+', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant() switch
            {
                "control" or "ctrl" => "ctrl",
                "windows" or "win" or "lwin" or "rwin" => "win",
                "escape" => "esc",
                "delete" => "del",
                _ => t.ToLowerInvariant()
            });
        return string.Join("+", tokens);
    }
}
