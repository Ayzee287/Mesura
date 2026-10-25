using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;
using Mesura.Core.Config;
using Mesura.Core.Windows;

namespace Mesura.App;

/// <summary>
/// The app's one source of text (redesign 2026-10 §8). Each language is two embedded JSON tables: the interface strings
/// (<c>Localization\{lang}.json</c>, flat keys) and, for a language other than English, the catalog texts
/// (<c>Localization\catalog-{lang}.json</c>: operation names, summaries, descriptions, limitations, steps, presets,
/// categories, app notes and the names of the app settings Mesura carries, by id). English catalog text comes from the catalogs themselves, so an entry with no translation, or one
/// from an edited catalog, shows its English text rather than nothing. XAML binds through <see cref="TExtension"/>, code calls
/// <see cref="Get"/>, <see cref="Format"/> and <see cref="Plural"/>. Registry paths, command lines, winget output and the
/// CLI stay in English. Adding a language is adding its two files and a line in <see cref="Languages"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>The languages the app offers, by code, with their own names.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [("en", "English"), ("ru", "Русский")];

    private Dictionary<string, string> _strings = [];
    private Dictionary<string, string> _english = [];
    private CatalogText _catalog = new();

    private Loc()
    {
        _english = Strings("en");
        Use(DefaultLanguage());
    }

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed, so view models can rebuild the text they composed.</summary>
    public event Action? LanguageChanged;

    public string Language { get; private set; } = "en";

    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "ru" ? "ru-RU" : "en-GB");

    /// <summary>The interface string for a key; the English one when this language lacks it; the key itself when neither has it.</summary>
    public string this[string key] => _strings.TryGetValue(key, out var text) ? text : _english.TryGetValue(key, out var en) ? en : key;

    public static string Get(string key) => Instance[key];

    public static string Format(string key, params object[] args) => string.Format(Instance.Culture, Instance[key], args);

    /// <summary>
    /// A counted phrase: <c>{key}.one</c>, <c>.few</c>, <c>.many</c> (Russian) or <c>.one</c>, <c>.other</c> (English),
    /// each with <c>{0}</c> for the number.
    /// </summary>
    public static string Plural(string key, int count, params object[] more)
    {
        var form = Instance.Language == "ru" ? RussianForm(count) : count == 1 ? "one" : "other";
        var pattern = Instance._strings.TryGetValue($"{key}.{form}", out var text) ? text
            : Instance._strings.TryGetValue($"{key}.other", out var other) ? other
            : Instance[$"{key}.{(count == 1 ? "one" : "other")}"];
        return string.Format(Instance.Culture, pattern, [count, .. more]);
    }

    private static string RussianForm(int n)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        return mod10 == 1 && mod100 != 11 ? "one" : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
    }

    /// <summary>Switches the language; every binding through <see cref="TExtension"/> updates at once.</summary>
    public void Use(string language)
    {
        var code = Languages.Any(l => l.Code == language) ? language : "en";
        Language = code;
        _strings = code == "en" ? _english : Strings(code);
        _catalog = code == "en" ? new CatalogText() : Catalog(code);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }

    /// <summary>Russian when Windows' display language is Russian, otherwise English.</summary>
    public static string DefaultLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";

    // ---- Catalog text ------------------------------------------------------------------------------------------------

    public static string Name(WindowsOperation op) => Instance._catalog.Op(op.Id)?.Name ?? op.Name;

    public static string Summary(WindowsOperation op) =>
        Instance._catalog.Op(op.Id)?.Summary ?? (op.Summary.Length > 0 ? op.Summary : op.Description);

    public static string Description(WindowsOperation op) => Instance._catalog.Op(op.Id)?.Description ?? op.Description;

    public static string Limitations(WindowsOperation op) => Instance._catalog.Op(op.Id)?.Limitations ?? op.Limitations;

    public static IReadOnlyList<string> Steps(WindowsOperation op) =>
        Instance._catalog.Op(op.Id)?.Steps is { Count: > 0 } steps && steps.Count == (op.Manual?.Steps.Count ?? 0) ? steps : op.Manual?.Steps ?? [];

    public static string Consequence(WindowsOperation op) => Instance._catalog.Op(op.Id)?.Consequence ?? op.Observation?.Consequence ?? "";

    public static string Restore(WindowsOperation op) => Instance._catalog.Op(op.Id)?.Restore ?? op.Observation?.Restore ?? "";

    public static string Name(WindowsPreset preset) => Instance._catalog.Preset(preset.Id)?.Name ?? preset.Name;

    public static string Summary(WindowsPreset preset) =>
        Instance._catalog.Preset(preset.Id)?.Summary ?? (preset.Summary.Length > 0 ? preset.Summary : preset.Description);

    public static string Description(WindowsPreset preset) => Instance._catalog.Preset(preset.Id)?.Description ?? preset.Description;

    /// <summary>A catalog category or app category, translated when the table has it.</summary>
    public static string Category(string category) =>
        Instance._catalog.Categories?.TryGetValue(category, out var text) == true ? text : category;

    /// <summary>An app's note, translated when the table has it.</summary>
    public static string AppNote(string appId, string note) =>
        Instance._catalog.AppNotes?.TryGetValue(appId, out var text) == true ? text : note;

    public static string Impact(OperationImpact impact) => Get($"Impact.{impact}");

    /// <summary>An app setting Mesura carries (Configurations), translated when the table has it.</summary>
    public static string Setting(ConfigSetting setting) =>
        Instance._catalog.ConfigSettings?.TryGetValue(setting.Id, out var text) == true ? text : setting.DisplayName;

    /// <summary>A category of data an app's transfer never carries, translated when the table has it.</summary>
    public static string Exclusion(ConfigExclusion exclusion) =>
        Instance._catalog.ConfigExclusions?.TryGetValue(exclusion.Name, out var text) == true ? text : exclusion.Name;

    // ---- Loading -----------------------------------------------------------------------------------------------------

    private static Stream? Resource(string name) =>
        typeof(Loc).Assembly.GetManifestResourceStream($"Mesura.App.Localization.{name}");

    private static Dictionary<string, string> Strings(string language)
    {
        using var stream = Resource($"{language}.json");
        if (stream is null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, Options) ?? [];
    }

    private static CatalogText Catalog(string language)
    {
        using var stream = Resource($"catalog-{language}.json");
        return stream is null ? new CatalogText() : JsonSerializer.Deserialize<CatalogText>(stream, Options) ?? new CatalogText();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>One language's catalog texts; any field may be missing.</summary>
    public sealed class CatalogText
    {
        public Dictionary<string, OpText>? Operations { get; set; }

        public Dictionary<string, PresetText>? Presets { get; set; }

        public Dictionary<string, string>? Categories { get; set; }

        public Dictionary<string, string>? AppNotes { get; set; }

        public Dictionary<string, string>? ConfigSettings { get; set; }

        public Dictionary<string, string>? ConfigExclusions { get; set; }

        public OpText? Op(string id) => Operations?.TryGetValue(id, out var text) == true ? text : null;

        public PresetText? Preset(string id) => Presets?.TryGetValue(id, out var text) == true ? text : null;
    }

    public sealed class OpText
    {
        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? Description { get; set; }

        public string? Limitations { get; set; }

        public List<string>? Steps { get; set; }

        public string? Consequence { get; set; }

        public string? Restore { get; set; }
    }

    public sealed class PresetText
    {
        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? Description { get; set; }
    }
}

/// <summary>
/// <c>{l:T Key}</c> in XAML: the interface string for a key, which follows the language when it changes.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
