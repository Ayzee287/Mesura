using System.Text.Json.Serialization;

namespace FpsUtility.Core.Raw;

/// <summary>
/// What one scan observed on one machine: raw observations only, no judgement (architecture §4.4).
/// It is the contract every later stage reads, and the platform seam (D009): another platform's
/// collector fills the same model.
/// </summary>
/// <remarks>
/// Records here compare lists and byte arrays by reference. Two snapshots are the same when their
/// canonical JSON (<see cref="Serialization.SnapshotJson"/>) is the same.
/// </remarks>
public sealed record RawSnapshot
{
    /// <summary>
    /// Bumped on any breaking change to this model or its JSON, including a field whose meaning changes
    /// under the same name (architecture §4.3). Version 2 (Milestone 1.5): <c>physicalDisks[].model</c> is the
    /// hardware model, no longer the editable friendly name; disks gained <c>location</c>; storage gained
    /// <c>disks</c>; Steam libraries gained <c>listedByLibraryFolders</c> and <c>isInstallFolder</c>.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    [JsonConstructor]
    public RawSnapshot(int schemaVersion, DateTimeOffset capturedAtUtc, AppInfo app, OsInfo os, bool elevated, SnapshotSources sources)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, $"Only schema version {CurrentSchemaVersion} exists.");
        }

        if (capturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The capture time is in UTC.", nameof(capturedAtUtc));
        }

        SchemaVersion = schemaVersion;
        CapturedAtUtc = capturedAtUtc;
        App = app ?? throw new ArgumentNullException(nameof(app));
        Os = os ?? throw new ArgumentNullException(nameof(os));
        Elevated = elevated;
        Sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public int SchemaVersion { get; }

    /// <summary>Run-dependent (§4.4).</summary>
    public DateTimeOffset CapturedAtUtc { get; }

    public AppInfo App { get; }

    /// <summary>A snapshot is only ever written on a supported platform (D011).</summary>
    public OsInfo Os { get; }

    /// <summary>Whether the collecting process was elevated; <c>false</c> for every MVP scan.</summary>
    public bool Elevated { get; }

    public SnapshotSources Sources { get; }
}

/// <param name="Name">Always <c>FpsUtility</c> (D007).</param>
/// <param name="Version">The app version, e.g. <c>0.1.0</c>.</param>
/// <param name="Commit">The git commit the app was built from.</param>
public sealed record AppInfo(string Name, string Version, Reading<string> Commit);

/// <summary>
/// The raw sources, one entry each, always in this fixed order whatever order the collectors
/// finish in (architecture §4.4). Every entry is present: a source that failed is
/// <c>unavailable</c> with a reason, never absent.
/// </summary>
public sealed record SnapshotSources(
    SourceEntry<DisplayData> Display,
    SourceEntry<EdidData> Edid,
    SourceEntry<AdapterData> Adapters,
    SourceEntry<SmbiosData> Smbios,
    SourceEntry<PcieData> Pcie,
    SourceEntry<StorageData> Storage,
    SourceEntry<SteamData> Steam,
    SourceEntry<PowerData> Power);
