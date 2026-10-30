using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mesura.Setup.Tests;

/// <summary>
/// A per-user install never asks for administrator rights (docs/security.md, owner's decision: no override switch), so every message that tells the user to
/// "restart as administrator" needs a <c>.PerUser</c> variant that says to install for all users (<c>Loc.AdminKey</c>), in both languages.
/// </summary>
public sealed class AdminMessageTests
{
    private static Dictionary<string, string> Load(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mesura.slnx")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir!.FullName, "src", "Mesura.App", "Localization", file);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path).TrimStart('﻿'))!;
    }

    private static readonly Regex Placeholders = new(@"\{\d+\}");

    private static string Ph(string text) => string.Join(',', Placeholders.Matches(text).Select(m => m.Value).Distinct().Order());

    [Fact]
    public void Every_per_user_message_has_its_plain_twin_with_the_same_placeholders_in_both_languages()
    {
        var en = Load("en.json");
        var ru = Load("ru.json");

        foreach (var key in en.Keys.Where(k => k.Contains(".PerUser", StringComparison.Ordinal) && !k.StartsWith("Admin.", StringComparison.Ordinal) && !k.StartsWith("Settings.ElevateExposed", StringComparison.Ordinal)))
        {
            var plain = key.Replace(".PerUser", "", StringComparison.Ordinal);
            Assert.True(en.ContainsKey(plain), $"{key} has no plain twin {plain}");
            Assert.Equal(Ph(en[plain]), Ph(en[key]));
        }

        foreach (var key in en.Keys.Where(k => k.Contains(".PerUser", StringComparison.Ordinal)))
        {
            // Russian has the counted forms .one/.few/.many where English has .one/.other.
            var forms = Regex.IsMatch(key, @"\.(one|other)$") ? new[] { "one", "few", "many" }.Select(f => Regex.Replace(key, @"\.(one|other)$", "." + f)) : [key];
            foreach (var ruKey in forms)
            {
                Assert.True(ru.ContainsKey(ruKey), $"ru.json lacks {ruKey}");
            }
        }
    }

    [Fact]
    public void A_message_that_says_restart_as_administrator_has_a_per_user_variant_unless_it_is_about_something_else()
    {
        var en = Load("en.json");
        // Strings that talk about an already elevated Mesura, or about elevation of a different kind, are not instructions to restart.
        string[] notInstructions = ["Common.ElevatedElsewhere", "Nav.AdminHint", "Settings.RunningElevated", "Settings.RestorePointHint", "Catalog.IgnoredElevated", "Activation.NeedsAdmin",
            // The start-up dialog's button: shown only where administrator mode is offered (a protected folder); the other dialog is the per-user one.
            "Start.Restart"];

        var instructions = en.Where(p => !p.Key.Contains(".PerUser", StringComparison.Ordinal) && !notInstructions.Contains(p.Key)
            && Regex.IsMatch(p.Value, @"(restart|run) (mesura )?(as|from an) administrator", RegexOptions.IgnoreCase)).Select(p => p.Key).ToList();

        Assert.NotEmpty(instructions);
        foreach (var key in instructions)
        {
            var variants = en.Keys.Any(k => k.StartsWith(key + ".PerUser", StringComparison.Ordinal))
                || (Regex.IsMatch(key, @"\.(one|other)$") && en.Keys.Any(k => k.StartsWith(Regex.Replace(key, @"\.(one|other)$", ".PerUser"), StringComparison.Ordinal)));
            Assert.True(variants, $"{key} tells the user to restart as administrator but has no .PerUser variant");
        }
    }

    [Fact]
    public void The_removed_override_switch_is_gone_from_both_languages()
    {
        Assert.DoesNotContain("Settings.ElevateAnyway", Load("en.json").Keys);
        Assert.DoesNotContain("Settings.ElevateAnyway", Load("ru.json").Keys);
    }
}
