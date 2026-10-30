namespace Mesura.Core.Windows;

/// <summary>
/// What a recorded change may point at when it is undone (0.3.0-rc2, docs/security.md). A history record is a file in the user's data
/// folder, which any program running as the user can write; an undo run as administrator that believed a record's registry path,
/// service name or task path would write wherever the file says. So an undo trusts a record only for the targets the operation named in
/// the record is <i>declared</i> to change in the catalog the program was built with (or the user's edited one when not elevated):
/// the same registry value, service, feature, scheduled task, power setting or package. The values to restore still come from the
/// record (they are the user's own earlier values).
/// </summary>
public static class RevertTrust
{
    /// <summary>Whether <paramref name="operation"/> declares the target of <paramref name="change"/>.</summary>
    public static bool Declares(this WindowsOperation operation, RecordedChange change)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(change);
        return change.Kind switch
        {
            ChangeKind.Registry => RegistryDeclared(operation, change),
            ChangeKind.Service => operation.Services.Any(s => Same(s.Name, change.Name)),
            ChangeKind.Feature => operation.Features.Any(f => Same(f.Name, change.Name)),
            ChangeKind.Task => operation.Tasks.Any(t => Same(t.Path, change.Path)),
            ChangeKind.TcpGlobal => operation.TcpSettings.Any(t => Same(t.Name, change.Name)),
            ChangeKind.SystemSetting => operation.SystemSettings.Any(s => Same(s.Kind, change.Path) && Same(s.Name, change.Name)),
            ChangeKind.Appx => operation.AppxSettings.Any(a => change.Path is not null && change.Path.Contains(a.Name, StringComparison.OrdinalIgnoreCase)),
            ChangeKind.PowerSetting => operation.PowerSettings.Any(p => Same(p.Subgroup, change.Path) && Same(p.Setting, change.Name)),
            ChangeKind.PowerScheme => operation.PowerPlan is not null,
            ChangeKind.PowerSchemeCreated => operation.PowerPlan is { } plan
                && (Same(plan.SchemeGuid, change.After) || Same(plan.SchemeGuid, change.Before)),
            _ => false,
        };
    }

    private static bool RegistryDeclared(WindowsOperation operation, RecordedChange change)
    {
        if (change.Hive is null || change.Path is null || change.Name is null)
        {
            return false;
        }

        if (operation.Registry.Any(r => Same(r.Hive, change.Hive) && Same(r.Path, change.Path) && Same(r.Name, change.Name)))
        {
            return true;
        }

        foreach (var each in operation.RegistryEachSettings.Where(e => Same(e.Hive, change.Hive) && Same(e.Name, change.Name)))
        {
            var (parent, tail) = each.Split();
            if (!change.Path.StartsWith(parent + @"\", StringComparison.OrdinalIgnoreCase) || !change.Path.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var middle = change.Path[(parent.Length + 1)..^tail.Length];
            if (middle.Length > 0 && !middle.Contains('\\', StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
