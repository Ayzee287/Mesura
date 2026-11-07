using System.Text.Json;
using Mesura.Core.Bonus;
using Mesura.Core.Windows;

namespace Mesura.Core.Tests.Windows;

public sealed class CursorBonusTests
{
    private const string Folder = @"C:\Users\Someone\AppData\Local\Mesura\Setup\bonus\cursors\w11-light-2.2";
    private static readonly string Cursors = CursorPack.CursorsKey;

    private sealed class FakeFiles : ICursorFiles
    {
        public string Folder => CursorBonusTests.Folder;
        public bool Installed { get; set; }
        public int Installs { get; private set; }
        public string? Saved { get; set; }
        public bool FailInstall { get; set; }
        public bool AllPresent() => Installed;

        public void Install()
        {
            Installs++;
            if (FailInstall)
            {
                throw new IOException("disk full");
            }

            Installed = true;
        }

        public void Remove() => Installed = false;
        public string? ReadSaved() => Saved;
        public void WriteSaved(string json) => Saved = json;
        public void DeleteSaved() => Saved = null;
    }

    private sealed class FakeSession : ICursorSession
    {
        public int Reloads { get; private set; }
        public bool Result { get; set; } = true;

        public bool Reload()
        {
            Reloads++;
            return Result;
        }
    }

    private static string Key(string name) => $@"HKCU\{Cursors}\{name}";

    /// <summary>The cursors a fresh Windows 11 user has, as the Sandbox read them (2026-11-06): plain strings with absolute paths, the scheme's name in the default value.</summary>
    private static FakeWindows WindowsDefault()
    {
        var pc = new FakeWindows();
        pc.Registry[Key("")] = (RegistryKind.String, "Windows Default");
        foreach (var role in CursorPack.Roles)
        {
            pc.Registry[Key(role.ValueName)] = (RegistryKind.String, $@"C:\Windows\cursors\aero_{role.ValueName.ToLowerInvariant()}.cur");
        }

        return pc;
    }

    /// <summary>A PC where another tool wrote the roles as expandable strings (<c>%SystemRoot%</c>): the real reader returns those without a kind.</summary>
    private static FakeWindows ExpandableDefault()
    {
        var pc = WindowsDefault();
        foreach (var role in CursorPack.Roles)
        {
            pc.Registry[Key(role.ValueName)] = (RegistryKind.String, $@"%SystemRoot%\cursors\aero_{role.ValueName.ToLowerInvariant()}.cur");
            pc.ExpandKeys.Add(Key(role.ValueName));
        }

        return pc;
    }

    private static (CursorBonus Bonus, FakeFiles Files, FakeSession Session) Make(FakeWindows pc)
    {
        var files = new FakeFiles();
        var session = new FakeSession();
        return (new CursorBonus(pc, pc, files, session), files, session);
    }

    [Fact]
    public void The_pack_names_seventeen_roles_and_seventeen_files()
    {
        Assert.Equal(17, CursorPack.Roles.Count);
        Assert.Equal(17, CursorPack.Files.Count);
        Assert.Equal(CursorPack.Roles.Count, CursorPack.Roles.Select(r => r.ValueName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("", CursorPack.ValueNames);
        Assert.Equal(17, CursorPack.SchemeValue(Folder).Split(',').Length);
    }

    [Fact]
    public void Apply_points_every_role_at_the_pack_as_expandable_strings_and_adds_the_scheme()
    {
        var pc = WindowsDefault();
        var (bonus, files, session) = Make(pc);

        var result = bonus.Apply();

        Assert.True(result.Changed);
        Assert.True(result.Reloaded);
        Assert.Equal(1, session.Reloads);
        Assert.Equal(CursorBonusState.Applied, bonus.State());
        Assert.True(files.Installed);
        Assert.Equal(CursorPack.SchemeName, pc.Registry[Key("")].Value);
        foreach (var role in CursorPack.Roles)
        {
            Assert.Equal(Path.Combine(Folder, role.File), pc.Registry[Key(role.ValueName)].Value);
            Assert.Contains(Key(role.ValueName), pc.ExpandWrites);
        }

        Assert.Equal(CursorPack.SchemeValue(Folder), pc.Registry[$@"HKCU\{CursorPack.SchemesKey}\{CursorPack.SchemeName}"].Value);
        Assert.NotNull(files.Saved);
    }

    [Fact]
    public void Restore_puts_the_windows_scheme_back_exactly_and_takes_the_pack_away()
    {
        var pc = WindowsDefault();
        var before = pc.Registry.ToDictionary(p => p.Key, p => p.Value);
        var (bonus, files, _) = Make(pc);
        bonus.Apply();

        var result = bonus.Restore();

        Assert.True(result.Changed);
        Assert.Equal(before, pc.Registry.ToDictionary(p => p.Key, p => p.Value));
        Assert.False(files.Installed);
        Assert.Null(files.Saved);
        Assert.Equal(CursorBonusState.NotApplied, bonus.State());
        // Plain strings come back plain: the registry ends up with the kinds it began with.
        Assert.Empty(pc.ExpandKeys);
    }

    [Fact]
    public void Expandable_values_come_back_expandable_and_plain_ones_plain()
    {
        var pc = ExpandableDefault();
        pc.Registry[Key("")] = (RegistryKind.String, "Mine"); // the default value stays a plain string
        var before = pc.Registry.ToDictionary(p => p.Key, p => p.Value);
        var expandBefore = pc.ExpandKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (bonus, _, _) = Make(pc);
        bonus.Apply();
        Assert.All(CursorPack.Roles, r => Assert.Contains(Key(r.ValueName), pc.ExpandKeys));

        bonus.Restore();

        Assert.Equal(before, pc.Registry.ToDictionary(p => p.Key, p => p.Value));
        Assert.Equal(expandBefore, pc.ExpandKeys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(Key(""), pc.ExpandKeys);
    }

    [Fact]
    public void Values_that_were_never_there_are_deleted_again_on_restore()
    {
        var pc = new FakeWindows(); // no cursor values at all
        var (bonus, _, _) = Make(pc);
        bonus.Apply();
        Assert.Equal(CursorBonusState.Applied, bonus.State());

        bonus.Restore();

        Assert.DoesNotContain(pc.Registry.Keys, k => k.StartsWith($@"HKCU\{Cursors}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_role_the_user_changed_after_applying_is_left_alone_by_restore()
    {
        var pc = WindowsDefault();
        var (bonus, files, _) = Make(pc);
        bonus.Apply();
        pc.Registry[Key("Hand")] = (RegistryKind.String, @"C:\Mine\hand.cur");

        Assert.Equal(CursorBonusState.Changed, bonus.State());
        bonus.Restore();

        Assert.Equal(@"C:\Mine\hand.cur", pc.Registry[Key("Hand")].Value);
        Assert.Equal(@"C:\Windows\cursors\aero_arrow.cur", pc.Registry[Key("Arrow")].Value);
        Assert.False(files.Installed);
    }

    [Fact]
    public void Applying_twice_changes_nothing_the_second_time_and_keeps_the_saved_cursors()
    {
        var pc = WindowsDefault();
        var (bonus, files, session) = Make(pc);
        bonus.Apply();
        var saved = files.Saved;
        pc.Writes.Clear();

        var again = bonus.Apply();

        Assert.False(again.Changed);
        Assert.Empty(pc.Writes);
        Assert.Equal(saved, files.Saved);
        Assert.Equal(1, session.Reloads);
        Assert.Equal(1, files.Installs);
    }

    [Fact]
    public void A_failure_midway_puts_everything_back_and_leaves_no_files_or_saved_state()
    {
        var pc = WindowsDefault();
        var before = pc.Registry.ToDictionary(p => p.Key, p => p.Value);
        pc.RefuseWrite.Add(Key("SizeAll"));
        var (bonus, files, session) = Make(pc);

        Assert.Throws<UnauthorizedAccessException>(() => bonus.Apply());

        Assert.Equal(before, pc.Registry.ToDictionary(p => p.Key, p => p.Value));
        Assert.False(files.Installed);
        Assert.Null(files.Saved);
        Assert.Equal(0, session.Reloads);
    }

    [Fact]
    public void A_file_that_cannot_be_written_stops_before_any_value_changes()
    {
        var pc = WindowsDefault();
        var before = pc.Registry.ToDictionary(p => p.Key, p => p.Value);
        var (bonus, files, _) = Make(pc);
        files.FailInstall = true;

        Assert.Throws<IOException>(() => bonus.Apply());

        Assert.Equal(before, pc.Registry.ToDictionary(p => p.Key, p => p.Value));
        Assert.Null(files.Saved);
    }

    [Fact]
    public void An_unknown_signed_in_user_blocks_both_directions_without_writing()
    {
        var pc = WindowsDefault();
        pc.User = new UserContext(null, "", "no session user");
        var (bonus, files, _) = Make(pc);
        files.Saved = "{}";
        pc.Writes.Clear();

        Assert.NotNull(bonus.BlockedReason);
        Assert.Throws<InvalidOperationException>(() => bonus.Apply());
        Assert.Throws<InvalidOperationException>(() => bonus.Restore());
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void Restore_without_anything_saved_says_so_and_writes_nothing()
    {
        var pc = WindowsDefault();
        var (bonus, _, _) = Make(pc);
        pc.Writes.Clear();

        Assert.False(bonus.CanRestore);
        Assert.Throws<InvalidOperationException>(() => bonus.Restore());
        Assert.Empty(pc.Writes);
    }

    [Fact]
    public void A_saved_file_with_a_foreign_value_name_or_another_pack_is_not_trusted()
    {
        foreach (var json in new[]
        {
            """{"Pack":"w11-light-2.2","SavedAtUtc":"x","Values":[{"Name":"Software\\Run","Existed":true,"Value":"x"}],"Scheme":{"Name":"W11 Cursor Light Free v2.2 by Jepri Creations","Existed":false,"Value":null}}""",
            """{"Pack":"other","SavedAtUtc":"x","Values":[],"Scheme":{"Name":"W11 Cursor Light Free v2.2 by Jepri Creations","Existed":false,"Value":null}}""",
            """{"Pack":"w11-light-2.2","SavedAtUtc":"x","Values":[],"Scheme":{"Name":"Another scheme","Existed":true,"Value":"y"}}""",
            "not json",
        })
        {
            var pc = WindowsDefault();
            var (bonus, files, _) = Make(pc);
            files.Saved = json;
            pc.Writes.Clear();

            Assert.Throws<InvalidOperationException>(() => bonus.Restore());
            Assert.Empty(pc.Writes);
        }
    }

    [Fact]
    public void The_saved_cursors_survive_a_round_trip_through_json_text()
    {
        var pc = WindowsDefault();
        var (bonus, files, _) = Make(pc);
        bonus.Apply();

        using var document = JsonDocument.Parse(files.Saved!);
        Assert.Equal(CursorPack.Id, document.RootElement.GetProperty("Pack").GetString());
        Assert.Equal(CursorPack.ValueNames.Count, document.RootElement.GetProperty("Values").GetArrayLength());
    }
}
