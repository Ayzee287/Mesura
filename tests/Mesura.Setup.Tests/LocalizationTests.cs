using System.Text.Json;
using System.Text.RegularExpressions;
using Mesura.Core.Apps;
using Mesura.Core.Windows;

namespace Mesura.Setup.Tests;

/// <summary>
/// The app's string tables (src/Mesura.App/Localization) stay complete and consistent: every English key has its
/// Russian form, placeholders match, counted phrases have every plural form, and every built-in catalog entry and
/// preset has its Russian text, with the same number of steps. The files are read from the source tree.
/// </summary>
public sealed partial class LocalizationTests
{
    private static readonly string Folder = Path.Combine(RepoRoot(), "src", "Mesura.App", "Localization");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mesura.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Mesura.slnx) wasn't found above the test folder.");
    }

    private static readonly JsonSerializerOptions Options = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static Dictionary<string, string> Strings(string language) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Folder, $"{language}.json")), Options)!;

    private static readonly string[] PluralForms = [".one", ".few", ".many", ".other"];

    /// <summary>A key with its plural suffix removed.</summary>
    private static string Base(string key) => PluralForms.FirstOrDefault(key.EndsWith) is { } form ? key[..^form.Length] : key;

    private static IEnumerable<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order(StringComparer.Ordinal);

    [Fact]
    public void Every_english_string_has_a_russian_one_with_the_same_placeholders()
    {
        var en = Strings("en");
        var ru = Strings("ru");

        foreach (var (key, text) in en.Where(e => !PluralForms.Any(e.Key.EndsWith)))
        {
            Assert.True(ru.ContainsKey(key), $"ru.json lacks '{key}'");
            Assert.Equal(Placeholders(text), Placeholders(ru[key]));
        }

        Assert.Empty(ru.Keys.Select(Base).Except(en.Keys.Select(Base)));
    }

    [Fact]
    public void Counted_phrases_have_every_plural_form()
    {
        var en = Strings("en");
        var ru = Strings("ru");
        foreach (var counted in en.Keys.Where(k => PluralForms.Any(k.EndsWith)).Select(Base).Distinct())
        {
            Assert.True(en.ContainsKey(counted + ".one") && en.ContainsKey(counted + ".other"), $"en.json lacks a form of '{counted}'");
            foreach (var form in new[] { ".one", ".few", ".many" })
            {
                Assert.True(ru.ContainsKey(counted + form), $"ru.json lacks '{counted}{form}'");
                Assert.Contains("{0}", ru[counted + form], StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_catalog_entry_preset_and_category_has_its_russian_text()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "catalog-ru.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var operations = doc.RootElement.GetProperty("operations");
        var presets = doc.RootElement.GetProperty("presets");
        var categories = doc.RootElement.GetProperty("categories");

        foreach (var op in WindowsCatalog.Default.Operations)
        {
            Assert.True(operations.TryGetProperty(op.Id, out var text), $"catalog-ru.json lacks '{op.Id}'");
            Assert.True(text.TryGetProperty("name", out _) && text.TryGetProperty("summary", out _) && text.TryGetProperty("description", out _),
                $"'{op.Id}' lacks its name, summary or description");
            Assert.Equal(op.Limitations.Length > 0, text.TryGetProperty("limitations", out _));
            Assert.Equal(op.Manual?.Steps.Count ?? 0, text.TryGetProperty("steps", out var steps) ? steps.GetArrayLength() : 0);
            Assert.True(categories.TryGetProperty(op.Category, out _), $"catalog-ru.json lacks the category '{op.Category}'");
        }

        foreach (var preset in WindowsCatalog.Default.Presets)
        {
            Assert.True(presets.TryGetProperty(preset.Id, out _), $"catalog-ru.json lacks the preset '{preset.Id}'");
        }

        foreach (var category in AppCatalog.Default.Packages.Where(p => p.Listed).Select(p => p.Category).Distinct())
        {
            Assert.True(categories.TryGetProperty(category, out _), $"catalog-ru.json lacks the app category '{category}'");
        }

        var settings = doc.RootElement.GetProperty("configSettings");
        var exclusions = doc.RootElement.GetProperty("configExclusions");
        foreach (var port in Mesura.Core.Config.ConfigCatalog.All)
        {
            Assert.All(port.Settings, s => Assert.True(settings.TryGetProperty(s.Id, out _), $"catalog-ru.json lacks the app setting '{s.Id}'"));
            Assert.All(port.Exclusions, x => Assert.True(exclusions.TryGetProperty(x.Name, out _), $"catalog-ru.json lacks the exclusion '{x.Name}'"));
        }
    }

    /// <summary>
    /// Every key the app names in its source (<c>Loc.Get</c>, <c>Loc.Format</c>, <c>Loc.Plural</c>, <c>{l:T …}</c>) is in
    /// en.json: a missing key would show as the key itself. Keys built at run time are checked by their own tests.
    /// </summary>
    [Fact]
    public void Every_key_the_app_names_exists()
    {
        var en = Strings("en");
        var app = Path.Combine(RepoRoot(), "src", "Mesura.App");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in UsedKey().Matches(text))
            {
                var (kind, key) = (m.Groups["kind"].Value, m.Groups["key"].Value);
                var present = kind == "Plural" ? en.ContainsKey(key + ".one") && en.ContainsKey(key + ".other") : en.ContainsKey(key);
                if (!present && key != "Key")
                {
                    missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// The keys the BIOS &amp; Firmware page builds at run time: one observation per key the analyzer can give, a status, a certainty, a risk and a change
    /// per kind, each in both languages.
    /// </summary>
    [Fact]
    public void Keys_built_from_the_firmware_analysis_exist_in_both_languages()
    {
        var analyzer = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Mesura.Core", "Firmware", "FirmwareAnalyzer.cs"));
        var observations = Regex.Matches(analyzer, "\"((?:memory|rebar|bios|boot|sb|tpm|virt|boost|pcie)\\.[a-z0-9-]+|unreadable)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(observations.Count > 25, "the scan found too few observation keys: " + observations.Count);
        var snapshot = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Mesura.Core", "Firmware", "FirmwareSnapshot.cs"));
        var changes = Regex.Matches(snapshot, "(?:Text|Flag|Number)\\(\"([a-z.-]+)\"").Select(m => m.Groups[1].Value).Append("microcode").Distinct().ToList();
        Assert.True(changes.Count >= 8, "the scan found too few change keys: " + changes.Count);

        var keys = observations.Select(k => "Firmware.Obs." + k)
            .Concat(changes.Select(k => "Firmware.Change." + k))
            .Concat(Enum.GetNames<Mesura.Core.Firmware.FirmwareStatus>().Select(n => "Firmware.Status." + n))
            .Concat(Enum.GetNames<Mesura.Core.Firmware.FirmwareCertainty>().Select(n => "Firmware.Certainty." + n))
            .Concat(Enum.GetNames<Mesura.Core.Firmware.FirmwareRisk>().Select(n => "Firmware.Risk." + n));
        foreach (var lang in new[] { "en", "ru" })
        {
            var strings = Strings(lang);
            Assert.All(keys, key => Assert.True(strings.ContainsKey(key), $"{lang}.json lacks {key}"));
        }
    }

    /// <summary>The keys built at run time from the setup's and NVIDIA's own values all exist.</summary>
    [Fact]
    public void Keys_built_from_setup_and_nvidia_values_exist()
    {
        var en = Strings("en");
        var ru = Strings("ru");
        var keys = Enum.GetNames<Mesura.Core.Setup.StepKind>().Select(k => $"Setup.Step.{k}")
            .Concat(Enum.GetNames<Mesura.Core.Setup.StepState>().Select(s => $"Setup.State.{s}"))
            .Append("Setup.State.Running")
            .Concat(Mesura.Core.Nvidia.NvidiaComponents.All.Select(c => $"Nvidia.Rule.{c.Id}"))
            .Concat(Mesura.Core.Nvidia.NvidiaTweaks.All.Select(t => $"Nvidia.Tweak.{t.Id}"))
            .Concat(new uint[] { 0xC004F074, 0xC004C008, 0xC004C003, 0xC004F050, 0xC004C020, 0xC004F014, 0xC004F00F, 0x8007232B, 0x80070005,
                    0xC004F009, 0x803F7001, 0xC004C060, 0xC004E016, 0x80072F8F, 0x12345678 }
                .SelectMany(code => new[] { Mesura.Core.Licensing.ActivationErrors.Key(code, true), Mesura.Core.Licensing.ActivationErrors.Key(code, false) }));
        Assert.All(keys, key => Assert.True(en.ContainsKey(key) && ru.ContainsKey(key), $"'{key}' is missing"));
    }

    /// <summary>
    /// The keys the quality pass builds at run time (2026-10-24): every status word a setting can show, every impact,
    /// benefit and risk level, the follow-up for each kind of failed Windows step, every winget failure code Mesura
    /// knows, both settings sources' states on Configurations and each way a profile is remembered.
    /// </summary>
    [Fact]
    public void Keys_built_from_status_risk_benefit_and_failure_values_exist()
    {
        var en = Strings("en");
        var ru = Strings("ru");
        var keys = Enum.GetNames<OperationState>().Concat(new[] { "Checking", "AppliedNow", "Failed", "NeedsAdmin" }).Select(s => $"State.{s}")
            .Concat(Enum.GetNames<OperationImpact>().Select(i => $"Impact.{i}"))
            .Concat(Enum.GetNames<OperationBenefit>().Select(b => $"Benefit.{b}"))
            .Concat(new[] { "Low", "Moderate", "High" }.Select(r => $"Risk.{r}"))
            .Concat(new[] { WindowsOpStatus.Failed, WindowsOpStatus.Unknown, WindowsOpStatus.Blocked }.Select(s => $"FollowUp.Step{s}"))
            .Concat(Enum.GetNames<WindowsOpStatus>().Select(s => $"Step.{s}"))
            .Concat(Enum.GetNames<InstallStatus>().Select(s => $"Install.{s}"))
            .Concat(new[] { "8A150014", "8A150010", "8A150011", "8A150101", "8A150103", "8A150102", "8A150104", "8A150110", "8A150105", "8A150107", "8A15010A", "8A15010F" }
                .Select(code => $"Winget.{code}"))
            .Append("Winget.Other")
            .Concat(new[] { "UserUnknown", "ShellUnreachable", "OtherAccount", "Other" }.Select(r => $"Winget.Refused.{r}"))
            .Concat(new[] { "Defaults", "Saved" }.SelectMany(s => new[] { "InPlace", "AppliedNow", "Pending", "NotHere" }.Select(state => $"Config.{s}.{state}")))
            .Concat(new[] { "Saved", "Opened", "Restored" }.Select(a => $"Profiles.Recent{a}"))
            .Concat(Enum.GetNames<Mesura.Core.Setup.RunStatus>().Select(s => $"Activity.Status.{s}"));
        Assert.All(keys, key => Assert.True(en.ContainsKey(key) && ru.ContainsKey(key), $"'{key}' is missing"));
    }

    /// <summary>Every winget code that ends an install as failed has words of its own (not only "winget reported an error").</summary>
    [Fact]
    public void Every_failed_install_code_winget_knows_has_words()
    {
        var en = Strings("en");
        foreach (var code in new uint[] { 0x8A150014, 0x8A150010, 0x8A150011, 0x8A150101, 0x8A150103, 0x8A150102, 0x8A150104, 0x8A150110, 0x8A150105, 0x8A150107, 0x8A15010A, 0x8A15010F })
        {
            var (status, detail) = WingetOutput.DescribeInstall(unchecked((int)code));
            Assert.Equal(InstallStatus.Failed, status);
            Assert.Contains(code.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), detail, StringComparison.OrdinalIgnoreCase);
            Assert.True(en.ContainsKey($"Winget.{code:X8}"), $"Winget.{code:X8}");
        }
    }

    [GeneratedRegex(@"Loc\.(?<kind>Get|Format|Plural)\(""(?<key>[A-Za-z0-9_.\-]+)""|\{l:T (?<key>[A-Za-z0-9_.\-]+)\}")]
    private static partial Regex UsedKey();

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();
}
