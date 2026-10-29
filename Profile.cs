using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarDogsOverlay;

/// <summary>One cloned HUD region. All coordinates are in reference-resolution pixels
/// (5120x1440 by default) relative to the game's client area.</summary>
public sealed class Segment
{
    public string Name { get; set; } = "New segment";
    public string Color { get; set; } = "#FFFFFF";
    public bool Enabled { get; set; } = true;

    public int SrcX { get; set; }
    public int SrcY { get; set; }
    public int SrcW { get; set; } = 200;
    public int SrcH { get; set; } = 100;

    public int DestCenterX { get; set; } = 2560;
    public int DestCenterY { get; set; } = 720;

    /// <summary>Clone size relative to the source (0.5 = half size).</summary>
    public double Scale { get; set; } = 0.5;

    /// <summary>Overlay opacity in percent (10-100).</summary>
    public int Opacity { get; set; } = 100;

    [JsonIgnore] public Rectangle Source => new(SrcX, SrcY, Math.Max(1, SrcW), Math.Max(1, SrcH));

    [JsonIgnore]
    public Rectangle Dest
    {
        get
        {
            int w = Math.Max(1, (int)Math.Round(SrcW * Scale));
            int h = Math.Max(1, (int)Math.Round(SrcH * Scale));
            return new Rectangle(DestCenterX - w / 2, DestCenterY - h / 2, w, h);
        }
    }

    [JsonIgnore]
    public System.Drawing.Color DrawColor
    {
        get
        {
            try { return ColorTranslator.FromHtml(Color); }
            catch { return System.Drawing.Color.White; }
        }
    }
}

public sealed class OverlayProfile
{
    public string Name { get; set; } = "Default";

    /// <summary>Process names to look for, without ".exe". Checked in order.</summary>
    public List<string> ProcessNames { get; set; } = new() { "WARDOGS-Win64-Shipping", "WARDOGS" };

    /// <summary>Fallback: exact window title (trimmed, case-insensitive).</summary>
    public string WindowTitle { get; set; } = "WARDOGS";

    /// <summary>Resolution the coordinates were authored at. Scaled to the live client size.</summary>
    public int ReferenceWidth { get; set; } = 5120;
    public int ReferenceHeight { get; set; } = 1440;

    public bool AutoHideWhenGameNotFocused { get; set; } = true;

    public List<Segment> Segments { get; set; } = new();
}

internal static class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarDogsOverlay", "profiles");

    /// <summary>Writes the built-in default profile (and its preview image) on first run.</summary>
    public static void EnsureDefault()
    {
        Directory.CreateDirectory(Folder);
        if (Directory.EnumerateFiles(Folder, "*.json").Any()) return;
        File.WriteAllText(PathFor("Default"), ReadEmbeddedText("default.json"));
        WriteEmbeddedPreview("Default");
    }

    public static OverlayProfile LoadEmbeddedDefault() => Deserialize(ReadEmbeddedText("default.json"));

    public static void WriteEmbeddedPreview(string profileName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("default.png");
        if (s == null) return;
        using var f = File.Create(PreviewPathFor(profileName));
        s.CopyTo(f);
    }

    public static List<string> List() =>
        Directory.EnumerateFiles(Folder, "*.json")
            .Select(p => Path.GetFileNameWithoutExtension(p) ?? "")
            .Where(n => n.Length > 0)
            .OrderBy(n => n.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static OverlayProfile Load(string name)
    {
        var p = Deserialize(File.ReadAllText(PathFor(name)));
        p.Name = name;
        return p;
    }

    public static void Save(OverlayProfile p) => File.WriteAllText(PathFor(p.Name), JsonSerializer.Serialize(p, Json));

    public static Segment Clone(Segment s) =>
        JsonSerializer.Deserialize<Segment>(JsonSerializer.Serialize(s, Json), Json) ?? new Segment();

    public static string PathFor(string name) => Path.Combine(Folder, Sanitize(name) + ".json");

    public static string PreviewPathFor(string name) => Path.Combine(Folder, Sanitize(name) + ".png");

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }

    private static OverlayProfile Deserialize(string json) =>
        JsonSerializer.Deserialize<OverlayProfile>(json, Json) ?? new OverlayProfile();

    private static string ReadEmbeddedText(string resource)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' is missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
