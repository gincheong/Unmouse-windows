using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Unmouse;

internal readonly record struct Modifiers(bool Ctrl, bool Shift, bool Alt, bool Win)
{
    public static Modifiers Current() => new(Down(0x11), Down(0x10), Down(0x12), Down(0x5B) || Down(0x5C));

    private static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
}

internal readonly record struct Hotkey(int Key, Modifiers Modifiers)
{
    public bool Matches(int key, Modifiers modifiers) => Key == key && Modifiers == modifiers;

    // "Ctrl+Shift+J" 형식. 마지막 항목이 키 이름이다.
    public static Hotkey? Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        bool ctrl = false, shift = false, alt = false, win = false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "win": win = true; break;
                default: return null;
            }
        }
        if (!KeyCodes.TryParse(parts[^1], out int key)) return null;
        return new Hotkey(key, new Modifiers(ctrl, shift, alt, win));
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.Ctrl) parts.Add("Ctrl");
        if (Modifiers.Alt) parts.Add("Alt");
        if (Modifiers.Shift) parts.Add("Shift");
        if (Modifiers.Win) parts.Add("Win");
        parts.Add(KeyCodes.Name(Key) ?? "?");
        return string.Join("+", parts);
    }
}

internal sealed record LabelStyle(
    string FontFamily, string FontWeight, double FontSize,
    // 글자 크기에 더하는 상자의 가로, 세로 길이
    Size Padding,
    Color Background, double BackgroundOpacity, Color Text, double CornerRadius, bool Uppercase, bool Pointer)
{
    public Typeface Typeface => Settings.LabelTypeface(FontFamily, FontWeight);
}

internal sealed record GridStyle(
    // 칸이 작으면 이 크기보다 작게 그린다.
    double FontSize,
    Color Background, double BackgroundOpacity, Color Text, Color Line, double LineOpacity);

internal static class Settings
{
    // 설치 없이 쓰는 앱이라 설정 파일을 exe와 같은 폴더에 둔다.
    public static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

    public const string MonospacedFontFamily = ".monospaced";
    public const string SystemFontFamily = ".system";

    private static readonly Dictionary<string, object> Defaults = new()
    {
        ["language"] = "en",
        ["clickHotkey"] = "Ctrl+'",
        ["includeTaskbar"] = true,
        ["adminHint"] = true,
        ["scrollEnabled"] = true,
        ["scrollHotkey"] = "Ctrl+Shift+J",
        ["scrollSpeed"] = 2.0,
        ["scrollArrowKeys"] = false,
        ["scrollWholeNotches"] = false,
        ["gridEnabled"] = true,
        ["gridHotkey"] = "Ctrl+Shift+'",
        ["labelCharacters"] = "sadfjklewcmpgh",
        ["labelFontFamily"] = MonospacedFontFamily,
        ["labelFontWeight"] = "bold",
        ["labelFontSize"] = 12.0,
        ["labelPaddingWidth"] = 6.0,
        ["labelPaddingHeight"] = 2.0,
        ["labelBackgroundColor"] = "#FFD60A",
        ["labelBackgroundOpacity"] = 1.0,
        ["labelTextColor"] = "#000000",
        ["labelCornerRadius"] = 3.0,
        ["labelUppercase"] = true,
        ["labelPointer"] = true,
        ["gridFontSize"] = 13.0,
        ["gridBackgroundColor"] = "#FFD60A",
        ["gridBackgroundOpacity"] = 0.9,
        ["gridTextColor"] = "#000000",
        ["gridLineColor"] = "#FFD60A",
        ["gridLineOpacity"] = 0.6,
    };
    public static readonly string[] GridStyleKeys =
    {
        "gridFontSize", "gridBackgroundColor", "gridBackgroundOpacity", "gridTextColor", "gridLineColor",
        "gridLineOpacity",
    };
    public static readonly string[] LabelStyleKeys =
    {
        "labelCharacters", "labelFontFamily", "labelFontWeight", "labelFontSize", "labelPaddingWidth",
        "labelPaddingHeight", "labelBackgroundColor", "labelBackgroundOpacity", "labelTextColor",
        "labelCornerRadius", "labelUppercase", "labelPointer",
    };

    private static readonly Dictionary<string, FontWeight> FontWeightTable = new()
    {
        ["light"] = FontWeights.Light, ["regular"] = FontWeights.Regular, ["medium"] = FontWeights.Medium,
        ["semibold"] = FontWeights.SemiBold, ["bold"] = FontWeights.Bold, ["heavy"] = FontWeights.ExtraBold,
        ["black"] = FontWeights.Black,
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static volatile Dictionary<string, JsonElement> values = new();
    private static FileSystemWatcher? watcher;
    private static bool configBroken;

    // 설정 파일을 읽는 스레드에서도 호출된다.
    public static event Action? Changed;

    // 키를 누를 때마다 읽는 값이라 값이 바뀔 때 한 번만 해석해 둔다.
    public static Hotkey ClickHotkey { get; private set; }
    public static Hotkey ScrollHotkey { get; private set; }
    public static Hotkey GridHotkey { get; private set; }
    public static IReadOnlyList<char> LabelCharacters { get; private set; } = Array.Empty<char>();

    public static string Language => Str("language");
    public static bool IncludeTaskbar => Flag("includeTaskbar");
    public static bool ScrollEnabled => Flag("scrollEnabled");
    public static double ScrollSpeed => Math.Max(Num("scrollSpeed"), 0.1);
    public static bool ScrollArrowKeys => Flag("scrollArrowKeys");
    public static bool ScrollWholeNotches => Flag("scrollWholeNotches");
    public static bool GridEnabled => Flag("gridEnabled");

    public static LabelStyle LabelStyle => new(
        Str("labelFontFamily"), Str("labelFontWeight"), Math.Max(Num("labelFontSize"), 6),
        new Size(Math.Max(Num("labelPaddingWidth"), 0), Math.Max(Num("labelPaddingHeight"), 0)),
        ColorOf("labelBackgroundColor"), Math.Clamp(Num("labelBackgroundOpacity"), 0, 1), ColorOf("labelTextColor"),
        Math.Max(Num("labelCornerRadius"), 0), Flag("labelUppercase"), Flag("labelPointer"));

    public static GridStyle GridStyle => new(
        Math.Max(Num("gridFontSize"), 6), ColorOf("gridBackgroundColor"),
        Math.Clamp(Num("gridBackgroundOpacity"), 0, 1), ColorOf("gridTextColor"), ColorOf("gridLineColor"),
        Math.Clamp(Num("gridLineOpacity"), 0, 1));

    public static IEnumerable<(string Key, string Title)> FontWeightOptions => new[]
    {
        ("light", Tr("Light", "가늘게")), ("regular", Tr("Regular", "보통")), ("medium", Tr("Medium", "중간")),
        ("semibold", Tr("Semibold", "약간 굵게")), ("bold", Tr("Bold", "굵게")), ("heavy", Tr("Heavy", "아주 굵게")),
        ("black", Tr("Black", "가장 굵게")),
    };

    // 설정 창에서 고른 언어의 문구를 돌려준다.
    public static string Tr(string english, string korean) => Language == "ko" ? korean : english;

    public static Typeface LabelTypeface(string family, string weight)
    {
        string name = family switch
        {
            MonospacedFontFamily => "Consolas",
            SystemFontFamily => "Segoe UI",
            _ => family,
        };
        var fontWeight = FontWeightTable.TryGetValue(weight, out var found) ? found : FontWeights.Bold;
        return new Typeface(new FontFamily(name), FontStyles.Normal, fontWeight, FontStretches.Normal);
    }

    public static void Load()
    {
        try
        {
            var loaded = new Dictionary<string, JsonElement>();
            if (File.Exists(ConfigPath))
            {
                var options = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                };
                using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath), options);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    loaded[property.Name] = property.Value.Clone();
                }
            }
            values = loaded;
            configBroken = false;
        }
        catch (JsonException)
        {
            // 손으로 고치다 깨진 파일을 덮어쓰지 않도록 다시 읽힐 때까지 저장하지 않는다.
            configBroken = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        Apply();
        Changed?.Invoke();
    }

    public static void Watch()
    {
        watcher = new FileSystemWatcher(AppContext.BaseDirectory, Path.GetFileName(ConfigPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        watcher.Changed += (_, _) => Load();
        watcher.Created += (_, _) => Load();
        watcher.Renamed += (_, _) => Load();
        watcher.Deleted += (_, _) => Load();
        watcher.EnableRaisingEvents = true;
    }

    public static void Set(string key, object value)
    {
        var copy = new Dictionary<string, JsonElement>(values)
        {
            [key] = JsonSerializer.SerializeToElement(value, WriteOptions),
        };
        Commit(copy);
    }

    public static void Reset(IEnumerable<string> keys)
    {
        var copy = new Dictionary<string, JsonElement>(values);
        foreach (string key in keys) copy.Remove(key);
        Commit(copy);
    }

    private static void Commit(Dictionary<string, JsonElement> updated)
    {
        values = updated;
        Apply();
        Save();
        Changed?.Invoke();
    }

    // 파일에는 기본값과 다른 키만 적는다.
    private static void Save()
    {
        if (configBroken) return;
        var current = values;
        var overrides = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, fallback) in Defaults)
        {
            if (current.TryGetValue(key, out var value) && value.GetRawText() != JsonSerializer.Serialize(fallback, WriteOptions))
            {
                overrides[key] = value;
            }
        }
        if (overrides.Count == 0 && !File.Exists(ConfigPath)) return;
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(overrides, WriteOptions) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Apply()
    {
        ClickHotkey = HotkeyOf("clickHotkey");
        ScrollHotkey = HotkeyOf("scrollHotkey");
        GridHotkey = HotkeyOf("gridHotkey");

        string fallback = (string)Defaults["labelCharacters"];
        var seen = new HashSet<char>();
        var characters = Str("labelCharacters").ToLowerInvariant()
            .Where(character => KeyCodes.Code(character) != null && seen.Add(character)).ToList();
        LabelCharacters = characters.Count >= 2 ? characters : fallback.ToList();
    }

    public static Hotkey HotkeyOf(string key)
    {
        return Hotkey.Parse(Str(key)) ?? Hotkey.Parse((string)Defaults[key])!.Value;
    }

    public static string Str(string key)
    {
        return values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : (string)Defaults[key];
    }

    public static double Num(string key)
    {
        return values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : Convert.ToDouble(Defaults[key]);
    }

    public static bool Flag(string key)
    {
        if (values.TryGetValue(key, out var value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return value.GetBoolean();
        }
        return (bool)Defaults[key];
    }

    public static Color ColorOf(string key)
    {
        return ParseColor(Str(key)) ?? ParseColor((string)Defaults[key])!.Value;
    }

    private static Color? ParseColor(string hex)
    {
        string digits = hex.StartsWith('#') ? hex[1..] : hex;
        if (digits.Length != 6) return null;
        try
        {
            uint value = Convert.ToUInt32(digits, 16);
            return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

// 키보드 배열이나 입력기와 무관하게 동작하도록 문자 대신 스캔 코드로 매칭한다.
// 확장 키는 0x100을 더한 값으로 구분한다.
internal static class KeyCodes
{
    public const int Escape = 0x01;
    public const int Backspace = 0x0E;
    public const int Enter = 0x1C;
    public const int Space = 0x39;
    public const int Up = 0x148;
    public const int Left = 0x14B;
    public const int Right = 0x14D;
    public const int Down = 0x150;

    private static readonly Dictionary<char, int> Table = new()
    {
        ['1'] = 0x02, ['2'] = 0x03, ['3'] = 0x04, ['4'] = 0x05, ['5'] = 0x06, ['6'] = 0x07, ['7'] = 0x08,
        ['8'] = 0x09, ['9'] = 0x0A, ['0'] = 0x0B,
        ['q'] = 0x10, ['w'] = 0x11, ['e'] = 0x12, ['r'] = 0x13, ['t'] = 0x14, ['y'] = 0x15, ['u'] = 0x16,
        ['i'] = 0x17, ['o'] = 0x18, ['p'] = 0x19,
        ['a'] = 0x1E, ['s'] = 0x1F, ['d'] = 0x20, ['f'] = 0x21, ['g'] = 0x22, ['h'] = 0x23, ['j'] = 0x24,
        ['k'] = 0x25, ['l'] = 0x26, [';'] = 0x27,
        ['z'] = 0x2C, ['x'] = 0x2D, ['c'] = 0x2E, ['v'] = 0x2F, ['b'] = 0x30, ['n'] = 0x31, ['m'] = 0x32,
        [','] = 0x33, ['.'] = 0x34, ['/'] = 0x35,
    };
    private static readonly Dictionary<int, char> Reverse = Table.ToDictionary(pair => pair.Value, pair => pair.Key);

    // 단축키에는 쓸 수 있지만 라벨 문자로는 쓰지 않는 키
    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["'"] = 0x28, ["["] = 0x1A, ["]"] = 0x1B, ["\\"] = 0x2B, ["-"] = 0x0C, ["="] = 0x0D, ["`"] = 0x29,
        ["Space"] = Space, ["Enter"] = Enter, ["Tab"] = 0x0F,
        ["F1"] = 0x3B, ["F2"] = 0x3C, ["F3"] = 0x3D, ["F4"] = 0x3E, ["F5"] = 0x3F, ["F6"] = 0x40,
        ["F7"] = 0x41, ["F8"] = 0x42, ["F9"] = 0x43, ["F10"] = 0x44, ["F11"] = 0x57, ["F12"] = 0x58,
    };
    private static readonly Dictionary<int, string> Names = Named.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static int? Code(char character) => Table.TryGetValue(character, out int code) ? code : null;

    public static char? Character(int code) => Reverse.TryGetValue(code, out char character) ? character : null;

    public static bool IsFunctionKey(int code) => code is (>= 0x3B and <= 0x44) or 0x57 or 0x58;

    public static string? Name(int code)
    {
        if (Names.TryGetValue(code, out string? name)) return name;
        return Reverse.TryGetValue(code, out char character) ? char.ToUpperInvariant(character).ToString() : null;
    }

    public static bool TryParse(string name, out int code)
    {
        if (Named.TryGetValue(name, out code)) return true;
        if (name.Length == 1 && Table.TryGetValue(char.ToLowerInvariant(name[0]), out code)) return true;
        code = 0;
        return false;
    }
}

internal static class LabelGenerator
{
    // 어떤 라벨도 다른 라벨의 접두사가 되지 않게 만든다. 그래야 마지막 글자를 치는 즉시 클릭할 수 있다.
    public static List<string> Labels(int count, IReadOnlyList<char> characters)
    {
        if (count <= 0) return new List<string>();
        var hints = new List<string> { "" };
        int offset = 0;
        while (hints.Count - offset < count || hints.Count == 1)
        {
            string hint = hints[offset];
            offset += 1;
            foreach (char character in characters) hints.Add(character + hint);
        }
        return hints.GetRange(offset, count).OrderBy(hint => hint, StringComparer.Ordinal)
            .Select(hint => new string(hint.Reverse().ToArray())).ToList();
    }

    // 이미 보여 준 라벨 뒤에 count개를 더한 목록을 돌려준다. 기존 라벨은 그대로 두거나 뒤에 글자만 붙여서,
    // 바뀌기 전 라벨을 입력해도 다른 대상이 선택되지 않는다.
    public static List<string> Extend(IReadOnlyList<string> existing, int count, IReadOnlyList<char> characters)
    {
        var labels = existing.ToList();
        int total = existing.Count + count;
        while (labels.Count < total)
        {
            var used = new HashSet<string>(labels);
            var prefixes = new HashSet<string> { "" };
            foreach (string label in labels)
            {
                for (int length = 1; length < label.Length; length++) prefixes.Add(label[..length]);
            }
            var free = prefixes.OrderBy(prefix => prefix.Length).ThenBy(prefix => prefix, StringComparer.Ordinal)
                .SelectMany(prefix => characters.Select(character => prefix + character))
                .Where(candidate => !used.Contains(candidate) && !prefixes.Contains(candidate))
                .Distinct().Take(total - labels.Count).ToList();
            labels.AddRange(free);
            if (labels.Count >= total || characters.Count < 2) break;
            // 빈 자리가 없으면 가장 짧은 라벨 하나에 글자를 붙여서 그 아래에 자리를 만든다.
            int shortest = labels.Min(label => label.Length);
            int extended = labels.FindLastIndex(label => label.Length == shortest);
            labels[extended] += characters[0];
        }
        return labels;
    }
}
