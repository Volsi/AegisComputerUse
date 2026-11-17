using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AegisComputerUse.Core.Security;

namespace AegisComputerUse.Core.Native;

public interface IInputAutomationService
{
    Task<string> ExecuteAsync(
        string functionName,
        JsonElement args,
        int screenWidth,
        int screenHeight,
        int originX,
        int originY,
        CancellationToken ct);
}

public sealed class InputAutomationService : IInputAutomationService
{
    private static readonly Dictionary<string, ushort> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["lcontrol"] = 0xA2, ["rcontrol"] = 0xA3,
        ["shift"] = 0x10, ["lshift"] = 0xA0, ["rshift"] = 0xA1,
        ["alt"] = 0x12, ["menu"] = 0x12, ["lalt"] = 0xA4, ["ralt"] = 0xA5,
        ["win"] = 0x5B, ["lwin"] = 0x5B, ["meta"] = 0x5B, ["super"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D,
        ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, [" "] = 0x20,
        ["backspace"] = 0x08, ["back"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["page_up"] = 0x21, ["pgup"] = 0x21,
        ["pagedown"] = 0x22, ["page_down"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["arrowup"] = 0x26,
        ["down"] = 0x28, ["arrowdown"] = 0x28,
        ["left"] = 0x25, ["arrowleft"] = 0x25,
        ["right"] = 0x27, ["arrowright"] = 0x27,
        ["capslock"] = 0x14,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73,
        ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["a"] = 0x41, ["b"] = 0x42, ["c"] = 0x43, ["d"] = 0x44, ["e"] = 0x45,
        ["f"] = 0x46, ["g"] = 0x47, ["h"] = 0x48, ["i"] = 0x49, ["j"] = 0x4A,
        ["k"] = 0x4B, ["l"] = 0x4C, ["m"] = 0x4D, ["n"] = 0x4E, ["o"] = 0x4F,
        ["p"] = 0x50, ["q"] = 0x51, ["r"] = 0x52, ["s"] = 0x53, ["t"] = 0x54,
        ["u"] = 0x55, ["v"] = 0x56, ["w"] = 0x57, ["x"] = 0x58, ["y"] = 0x59, ["z"] = 0x5A,
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39
    };

    private static readonly HashSet<ushort> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0xA3, 0xA5
    ];

    public async Task<string> ExecuteAsync(
        string functionName,
        JsonElement args,
        int screenWidth,
        int screenHeight,
        int originX,
        int originY,
        CancellationToken ct)
    {
        switch (functionName)
        {
            case "click":
            case "click_at":
            case "left_click":
            case "left_click_at":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);

                string btn = args.TryGetProperty("button", out var bEl) ? bEl.GetString()?.ToLowerInvariant() ?? "left" : "left";
                int count = args.TryGetProperty("click_count", out var cEl) && cEl.TryGetInt32(out int cnt) ? cnt : 1;

                if (btn == "right")
                {
                    ClickMouse(Win32Interop.MOUSEEVENTF_RIGHTDOWN, Win32Interop.MOUSEEVENTF_RIGHTUP);
                    return $"Right-clicked at ({px}, {py})";
                }
                if (btn == "middle")
                {
                    ClickMouse(Win32Interop.MOUSEEVENTF_MIDDLEDOWN, Win32Interop.MOUSEEVENTF_MIDDLEUP);
                    return $"Middle-clicked at ({px}, {py})";
                }
                if (count == 2 || btn == "double")
                {
                    ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                    await Task.Delay(55, ct).ConfigureAwait(false);
                    ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                    return $"Double-clicked at ({px}, {py})";
                }
                if (count == 3 || btn == "triple")
                {
                    for (int i = 0; i < 3; i++)
                    {
                        ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                        if (i < 2) await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                    return $"Triple-clicked at ({px}, {py})";
                }

                ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                return $"Clicked at ({px}, {py})";
            }

            case "double_click":
            case "double_click_at":
            case "double_click_coordinate":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);
                ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                await Task.Delay(55, ct).ConfigureAwait(false);
                ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                return $"Double-clicked at ({px}, {py})";
            }

            case "triple_click":
            case "triple_click_at":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);
                for (int i = 0; i < 3; i++)
                {
                    ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                    if (i < 2) await Task.Delay(50, ct).ConfigureAwait(false);
                }
                return $"Triple-clicked at ({px}, {py})";
            }

            case "right_click":
            case "right_click_at":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);
                ClickMouse(Win32Interop.MOUSEEVENTF_RIGHTDOWN, Win32Interop.MOUSEEVENTF_RIGHTUP);
                return $"Right-clicked at ({px}, {py})";
            }

            case "middle_click":
            case "middle_click_at":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);
                ClickMouse(Win32Interop.MOUSEEVENTF_MIDDLEDOWN, Win32Interop.MOUSEEVENTF_MIDDLEUP);
                return $"Middle-clicked at ({px}, {py})";
            }

            case "hover":
            case "hover_at":
            case "move":
            case "move_cursor":
            case "move_to":
            case "mouse_move":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);
                return $"Moved cursor to ({px}, {py})";
            }

            case "mouse_down":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);
                string btn = args.TryGetProperty("button", out var bEl) ? bEl.GetString()?.ToLowerInvariant() ?? "left" : "left";
                uint flag = btn switch
                {
                    "right" => Win32Interop.MOUSEEVENTF_RIGHTDOWN,
                    "middle" => Win32Interop.MOUSEEVENTF_MIDDLEDOWN,
                    _ => Win32Interop.MOUSEEVENTF_LEFTDOWN
                };
                SendMouseFlags(flag, 0);
                return $"Mouse down ({btn}) at ({px}, {py})";
            }

            case "mouse_up":
            {
                var (px, py) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                MoveCursor(px, py);
                string btn = args.TryGetProperty("button", out var bEl) ? bEl.GetString()?.ToLowerInvariant() ?? "left" : "left";
                uint flag = btn switch
                {
                    "right" => Win32Interop.MOUSEEVENTF_RIGHTUP,
                    "middle" => Win32Interop.MOUSEEVENTF_MIDDLEUP,
                    _ => Win32Interop.MOUSEEVENTF_LEFTUP
                };
                SendMouseFlags(flag, 0);
                return $"Mouse up ({btn}) at ({px}, {py})";
            }

            case "drag":
            case "drag_and_drop":
            case "drag_and_drop_at":
            {
                var (sx, sy) = ReadAndMapPoint(args, "x", "y", screenWidth, screenHeight, originX, originY);
                var (dx, dy) = ReadAndMapPoint(args, "destination_x", "destination_y", screenWidth, screenHeight, originX, originY);
                MoveCursor(sx, sy);
                await Task.Delay(40, ct).ConfigureAwait(false);
                SendMouseFlags(Win32Interop.MOUSEEVENTF_LEFTDOWN, 0);
                const int steps = 10;
                for (int i = 1; i <= steps; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int ix = sx + (dx - sx) * i / steps;
                    int iy = sy + (dy - sy) * i / steps;
                    MoveCursor(ix, iy);
                    await Task.Delay(18, ct).ConfigureAwait(false);
                }
                SendMouseFlags(Win32Interop.MOUSEEVENTF_LEFTUP, 0);
                return $"Dragged from ({sx},{sy}) to ({dx},{dy})";
            }

            case "type":
            case "type_text":
            case "type_text_at":
            case "write":
            {
                if (ActionSecurityGuard.TryExtractPoint(args, "x", "y", out int nx, out int ny))
                {
                    var (px, py) = ActionSecurityGuard.MapToScreen(nx, ny, screenWidth, screenHeight, originX, originY);
                    MoveCursor(px, py);
                    ClickMouse(Win32Interop.MOUSEEVENTF_LEFTDOWN, Win32Interop.MOUSEEVENTF_LEFTUP);
                    await Task.Delay(80, ct).ConfigureAwait(false);
                }

                string text = args.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? string.Empty : string.Empty;
                bool clearBefore = args.TryGetProperty("clear_before_typing", out var cEl) && cEl.GetBoolean();
                bool pressEnter = args.TryGetProperty("press_enter", out var eEl) && eEl.GetBoolean();

                if (clearBefore)
                {
                    SendKeyCombination("ctrl+a");
                    await Task.Delay(35, ct).ConfigureAwait(false);
                    SendSingleVirtualKey(0x08); // Backspace
                    await Task.Delay(35, ct).ConfigureAwait(false);
                }

                TypeUnicodeText(text);

                if (pressEnter)
                {
                    await Task.Delay(35, ct).ConfigureAwait(false);
                    SendSingleVirtualKey(0x0D);
                }

                return $"Typed {text.Length} chars: '{text}'";
            }

            case "key_combination":
            case "press_key":
            case "key_press":
            case "press_keys":
            case "hotkey":
            {
                string keys = ReadKeysString(args);
                SendKeyCombination(keys);
                return $"Pressed keys: {keys}";
            }

            case "scroll":
            case "scroll_at":
            {
                if (ActionSecurityGuard.TryExtractPoint(args, "x", "y", out int nx, out int ny))
                {
                    var (px, py) = ActionSecurityGuard.MapToScreen(nx, ny, screenWidth, screenHeight, originX, originY);
                    MoveCursor(px, py);
                }
                string dir = args.TryGetProperty("direction", out var dEl) ? dEl.GetString() ?? "down" : "down";
                int mag = args.TryGetProperty("magnitude", out var mEl) && mEl.TryGetInt32(out int m) ? Math.Clamp(m, 1, 2000)
                        : args.TryGetProperty("amount", out var aEl) && aEl.TryGetInt32(out int a) ? Math.Clamp(a, 1, 2000)
                        : 400;
                ExecuteScroll(dir, mag);
                return $"Scrolled {dir} ({mag})";
            }

            case "scroll_document":
            {
                string dir = args.TryGetProperty("direction", out var dEl) ? dEl.GetString() ?? "down" : "down";
                int mag = args.TryGetProperty("magnitude", out var mEl) && mEl.TryGetInt32(out int m) ? Math.Clamp(m, 1, 2000) : 500;
                ExecuteScroll(dir, mag);
                return $"Scrolled document {dir} ({mag})";
            }

            case "wait":
            case "wait_5_seconds":
            case "sleep":
            {
                int sec = args.TryGetProperty("seconds", out var sEl) && sEl.TryGetInt32(out int s) ? Math.Clamp(s, 1, 30) : 5;
                await Task.Delay(sec * 1000, ct).ConfigureAwait(false);
                return $"Waited {sec} seconds";
            }

            case "navigate":
            case "open_url":
            {
                string url = args.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? string.Empty : string.Empty;
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException("Only http/https URLs are permitted.");
                }
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return $"Opened browser URL on host {uri.Host}";
            }

            case "open_web_browser":
                Process.Start(new ProcessStartInfo("https://www.google.com") { UseShellExecute = true });
                return "Opened default web browser";

            case "go_back":
                SendKeyCombination("alt+left");
                return "Navigated back (Alt+Left)";

            case "go_forward":
                SendKeyCombination("alt+right");
                return "Navigated forward (Alt+Right)";

            default:
                throw new NotSupportedException($"Unsupported action '{functionName}'.");
        }
    }

    private static (int X, int Y) ReadAndMapPoint(
        JsonElement args, string xProp, string yProp,
        int screenWidth, int screenHeight, int originX, int originY)
    {
        if (ActionSecurityGuard.TryExtractPoint(args, xProp, yProp, out int nx, out int ny))
        {
            return ActionSecurityGuard.MapToScreen(nx, ny, screenWidth, screenHeight, originX, originY);
        }

        if (Win32Interop.GetCursorPos(out var pt))
        {
            return (pt.X, pt.Y);
        }

        return ActionSecurityGuard.MapToScreen(0, 0, screenWidth, screenHeight, originX, originY);
    }

    private static void MoveCursor(int px, int py)
    {
        int vx = Win32Interop.GetSystemMetrics(Win32Interop.SM_XVIRTUALSCREEN);
        int vy = Win32Interop.GetSystemMetrics(Win32Interop.SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, Win32Interop.GetSystemMetrics(Win32Interop.SM_CXVIRTUALSCREEN));
        int vh = Math.Max(1, Win32Interop.GetSystemMetrics(Win32Interop.SM_CYVIRTUALSCREEN));

        int absX = Math.Clamp((int)Math.Round((px - vx) * 65535.0 / Math.Max(1, vw - 1)), 0, 65535);
        int absY = Math.Clamp((int)Math.Round((py - vy) * 65535.0 / Math.Max(1, vh - 1)), 0, 65535);

        var move = new Win32Interop.INPUT
        {
            Type = Win32Interop.INPUT_MOUSE,
            Union = new Win32Interop.InputUnion
            {
                Mouse = new Win32Interop.MOUSEINPUT
                {
                    Dx = absX,
                    Dy = absY,
                    DwFlags = Win32Interop.MOUSEEVENTF_MOVE | Win32Interop.MOUSEEVENTF_ABSOLUTE | Win32Interop.MOUSEEVENTF_VIRTUALDESK
                }
            }
        };
        Win32Interop.SendInput(1, [move], Marshal.SizeOf<Win32Interop.INPUT>());
        Win32Interop.SetCursorPos(px, py);
    }

    private static void ClickMouse(uint downFlag, uint upFlag)
    {
        var inputs = new Win32Interop.INPUT[2];
        inputs[0] = new Win32Interop.INPUT
        {
            Type = Win32Interop.INPUT_MOUSE,
            Union = new Win32Interop.InputUnion { Mouse = new Win32Interop.MOUSEINPUT { DwFlags = downFlag } }
        };
        inputs[1] = new Win32Interop.INPUT
        {
            Type = Win32Interop.INPUT_MOUSE,
            Union = new Win32Interop.InputUnion { Mouse = new Win32Interop.MOUSEINPUT { DwFlags = upFlag } }
        };
        Win32Interop.SendInput(2, inputs, Marshal.SizeOf<Win32Interop.INPUT>());
    }

    private static void SendMouseFlags(uint flags, uint mouseData)
    {
        var input = new Win32Interop.INPUT
        {
            Type = Win32Interop.INPUT_MOUSE,
            Union = new Win32Interop.InputUnion
            {
                Mouse = new Win32Interop.MOUSEINPUT { DwFlags = flags, MouseData = mouseData }
            }
        };
        Win32Interop.SendInput(1, [input], Marshal.SizeOf<Win32Interop.INPUT>());
    }

    private static void ExecuteScroll(string direction, int magnitude)
    {
        int notches = Math.Max(1, magnitude / 100) * 120;
        switch (direction.ToLowerInvariant())
        {
            case "up":
                SendMouseFlags(Win32Interop.MOUSEEVENTF_WHEEL, unchecked((uint)notches));
                break;
            case "down":
                SendMouseFlags(Win32Interop.MOUSEEVENTF_WHEEL, unchecked((uint)(-notches)));
                break;
            case "left":
                SendMouseFlags(Win32Interop.MOUSEEVENTF_HWHEEL, unchecked((uint)(-notches)));
                break;
            case "right":
                SendMouseFlags(Win32Interop.MOUSEEVENTF_HWHEEL, unchecked((uint)notches));
                break;
        }
    }

    private static void TypeUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<Win32Interop.INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                inputs.Add(KeyEvent(0x0D, keyUp: false));
                inputs.Add(KeyEvent(0x0D, keyUp: true));
                continue;
            }
            inputs.Add(UnicodeEvent(ch, keyUp: false));
            inputs.Add(UnicodeEvent(ch, keyUp: true));
        }

        if (inputs.Count > 0)
        {
            Win32Interop.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Win32Interop.INPUT>());
        }
    }

    private static void SendSingleVirtualKey(ushort vk)
    {
        Win32Interop.SendInput(2, [KeyEvent(vk, false), KeyEvent(vk, true)], Marshal.SizeOf<Win32Interop.INPUT>());
    }

    private static void SendKeyCombination(string combo)
    {
        string[] tokens = combo.Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vks = new List<ushort>();
        foreach (string token in tokens)
        {
            if (KeyMap.TryGetValue(token, out ushort vk))
            {
                vks.Add(vk);
            }
            else if (token.Length == 1)
            {
                // Best-effort single character fallback
                TypeUnicodeText(token);
                return;
            }
        }

        if (vks.Count == 0) return;

        var inputs = new List<Win32Interop.INPUT>(vks.Count * 2);
        foreach (ushort vk in vks) inputs.Add(KeyEvent(vk, false));
        for (int i = vks.Count - 1; i >= 0; i--) inputs.Add(KeyEvent(vks[i], true));
        Win32Interop.SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Win32Interop.INPUT>());
    }

    private static Win32Interop.INPUT KeyEvent(ushort vk, bool keyUp)
    {
        uint flags = keyUp ? Win32Interop.KEYEVENTF_KEYUP : 0;
        if (ExtendedKeys.Contains(vk)) flags |= Win32Interop.KEYEVENTF_EXTENDEDKEY;
        return new Win32Interop.INPUT
        {
            Type = Win32Interop.INPUT_KEYBOARD,
            Union = new Win32Interop.InputUnion
            {
                Keyboard = new Win32Interop.KEYBDINPUT { WVk = vk, DwFlags = flags }
            }
        };
    }

    private static Win32Interop.INPUT UnicodeEvent(char ch, bool keyUp)
    {
        uint flags = Win32Interop.KEYEVENTF_UNICODE | (keyUp ? Win32Interop.KEYEVENTF_KEYUP : 0);
        return new Win32Interop.INPUT
        {
            Type = Win32Interop.INPUT_KEYBOARD,
            Union = new Win32Interop.InputUnion
            {
                Keyboard = new Win32Interop.KEYBDINPUT { WScan = ch, DwFlags = flags }
            }
        };
    }

    private static string ReadKeysString(JsonElement args)
    {
        if (args.TryGetProperty("keys", out var kEl))
        {
            if (kEl.ValueKind == JsonValueKind.String) return kEl.GetString() ?? string.Empty;
            if (kEl.ValueKind == JsonValueKind.Array)
            {
                return string.Join("+", kEl.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }
        return args.TryGetProperty("key", out var single) ? single.GetString() ?? string.Empty : string.Empty;
    }
}
