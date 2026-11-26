using System.Text.Json;

namespace Mesura.Setup.Tests;

/// <summary>
/// The Russian texts of the 0.9 Phase 3 operations (vault D057) say the same consequences as the English ones: what is lost, the restart, and for Prefer IPv4
/// that IPv6 stays on. LocalizationTests already requires every operation to have Russian text; this checks the content of these five.
/// </summary>
public sealed class CatalogPhase3LocalizationTests
{
    private static JsonElement Operations()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "Mesura.App", "Localization", "catalog-ru.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return doc.RootElement.GetProperty("operations").Clone();
            }
        }

        throw new InvalidOperationException("catalog-ru.json not found above the test folder");
    }

    [Theory]
    [InlineData("tasks.ceip-off", "Consolidator", "UsbCeip")]
    [InlineData("tasks.feedback-off", "DmClient", "Update SIUF strings")]
    [InlineData("services.windows-search-off", "Outlook", "при следующем запуске Windows")]
    [InlineData("features.virtual-machine-platform-off", "WSL 2", "перезагрузка")]
    [InlineData("network.prefer-ipv4", "IPv6 остаётся включённым", "Нужна перезагрузка")]
    // 2026-11-26 (D059): DiagTrack in Maximum says in Russian too that it is one service, not all of Windows' telemetry.
    [InlineData("services.diagnostic-tracking-off", "а не вся телеметрия Windows", "отмена возвращает прежний тип запуска")]
    public void Each_new_operation_has_russian_text_that_names_its_consequences(string id, string first, string second)
    {
        var entry = Operations().GetProperty(id);
        var all = string.Join(" ", new[] { "name", "summary", "description", "limitations" }.Select(k => entry.GetProperty(k).GetString()));

        Assert.All(new[] { "name", "summary", "description", "limitations" }, k => Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty(k).GetString()), $"{id}.{k}"));
        Assert.Contains(first, all, StringComparison.Ordinal);
        Assert.Contains(second, all, StringComparison.Ordinal);
    }

    [Fact]
    public void Virtual_machine_platform_in_russian_names_the_sandbox_and_other_virtual_machines()
    {
        var limitations = Operations().GetProperty("features.virtual-machine-platform-off").GetProperty("limitations").GetString()!;
        Assert.Contains("песочниц", limitations, StringComparison.Ordinal);
        Assert.Contains("виртуальные машины", limitations, StringComparison.Ordinal);
        Assert.Contains("не проверяет", limitations, StringComparison.Ordinal);
    }
}
