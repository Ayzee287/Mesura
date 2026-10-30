namespace Mesura.Core.Windows;

/// <summary>Where a program that starts with Windows is registered.</summary>
public enum StartupSource
{
    /// <summary>The signed-in user's <c>Run</c> key.</summary>
    UserRun,

    /// <summary>The machine's <c>Run</c> key (every user).</summary>
    MachineRun,

    /// <summary>The machine's 32-bit <c>Run</c> key.</summary>
    MachineRun32,

    /// <summary>The user's Startup folder.</summary>
    UserFolder,

    /// <summary>The Startup folder shared by every user.</summary>
    CommonFolder,
}

/// <summary>
/// A program that starts when the user signs in, and whether Windows will start it. Windows (Task Manager's Startup
/// tab and Settings > Apps > Startup) keeps the on/off switch in a <c>StartupApproved</c> key beside the program's own
/// registration: turning a program off writes a flag there and leaves the program and its entry untouched, so it can be
/// turned on again from the same places. Mesura uses the same flag, recorded like any other change.
/// </summary>
public sealed record StartupItem(StartupSource Source, string Name, string Command, bool Enabled)
{
    private const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string ExplorerMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Windows' own flag for "turned off": the first byte odd (03), a timestamp after it; the rest zero.</summary>
    public const string DisabledFlag = "03,00,00,00,00,00,00,00,00,00,00,00";

    public bool Machine => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;

    public string Hive => Machine ? "HKLM" : "HKCU";

    /// <summary>The <c>StartupApproved</c> key that holds this entry's flag.</summary>
    public string ApprovedPath => Source switch
    {
        StartupSource.UserRun => $@"{Explorer}\Run",
        StartupSource.UserFolder => $@"{Explorer}\StartupFolder",
        StartupSource.MachineRun => $@"{ExplorerMachine}\Run",
        StartupSource.MachineRun32 => $@"{ExplorerMachine}\Run32",
        _ => $@"{ExplorerMachine}\StartupFolder",
    };

    /// <summary>Whether a <c>StartupApproved</c> value turns the entry off: Windows treats an odd first byte as off.</summary>
    public static bool IsDisabledFlag(string? hexBytes)
    {
        if (string.IsNullOrWhiteSpace(hexBytes))
        {
            return false; // no flag: the entry runs
        }

        var first = hexBytes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && byte.TryParse(first, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var b) && (b & 1) == 1;
    }

    /// <summary>A stable id for the entry (where it is registered and what it is called).</summary>
    public string Id => $"startup.{Source.ToString().ToLowerInvariant()}.{Name.ToLowerInvariant()}";

    /// <summary>
    /// The operation that turns the entry off. It is an ordinary registry operation (the flag, as binary), so applying,
    /// recording and undoing go through the same engine as every other setting.
    /// </summary>
    public WindowsOperation ToOperation(string name, string summary, string description, string limitations) => new(
        Id, name, "Startup apps", description, RiskClass.UserPreference, Activation.SignOut, null, null, limitations,
        "Task Manager's Startup tab and Settings > Apps > Startup use the same StartupApproved flag.",
        [new RegistrySetting(Hive, ApprovedPath, Name, RegistryKind.Binary, DisabledFlag)],
        [], [], null, [], [], null, null, null, OperationImpact.Performance, summary,
        null, OperationBenefit.Preference, ValueBasis.Documented, OperationLevel.Safe);
}

/// <summary>Finds the programs registered to start with Windows. The platform component implements it (it reads only).</summary>
public interface IStartupSource
{
    IReadOnlyList<StartupItem> Scan();
}
