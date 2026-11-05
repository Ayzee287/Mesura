using Mesura.Core.Hardware;

namespace Mesura.Core.Profiles;

/// <summary>Whether an entry is applied with its profile (ticked) or only offered beside it (unticked).</summary>
public enum EntryRole
{
    /// <summary>Part of the profile: applied unless it can't apply here or the user unticks it.</summary>
    Default,

    /// <summary>Offered with the profile and never applied unless the user ticks it.</summary>
    Optional,
}

/// <summary>One group of a profile: a set of entries the user can switch as one. Names and summaries are English; the app translates them by id.</summary>
public sealed record ProfileGroup(string Id, string Name, string Summary);

/// <summary>
/// One operation in a profile: which group it belongs to, whether it is applied with the profile, why it is there, whether it has a cost the user
/// should see before applying, and the hardware it is for. An entry never says how to change anything: the operation does (the catalog's single place for
/// values, requirements and revert).
/// </summary>
/// <param name="Why">What the setting is for in this profile, in a sentence; never a promise of speed (no gain is claimed unless measured).</param>
/// <param name="SideEffects">Whether the operation has a consequence the user should read first (heat, noise, idle power, a function lost): the app shows its limitations beside the entry.</param>
/// <param name="When">Hardware this entry is for, beyond the operation's own requirements; <c>null</c> when it applies anywhere.</param>
public sealed record ProfileEntry(string OperationId, string GroupId, EntryRole Role, string Why, bool SideEffects, HardwareCondition? When);

/// <summary>
/// A profile (0.6): the entries of one level, in groups. Its id is the id of the catalog preset it corresponds to (<c>recommended-gaming</c>,
/// <c>maximum-performance</c>), which saved setup profiles and the command line keep using; its name and summary come from that preset.
/// </summary>
public sealed record OptimizationProfile(string Id, IReadOnlyList<ProfileGroup> Groups, IReadOnlyList<ProfileEntry> Entries)
{
    public IReadOnlyList<ProfileEntry> InGroup(string groupId) =>
        [.. Entries.Where(e => e.GroupId.Equals(groupId, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The entries applied with the profile, in file order: what the catalog preset of the same id lists.</summary>
    public IReadOnlyList<string> DefaultIds => [.. Entries.Where(e => e.Role == EntryRole.Default).Select(e => e.OperationId)];

    /// <summary>The entries offered unticked beside it.</summary>
    public IReadOnlyList<string> OptionalIds => [.. Entries.Where(e => e.Role == EntryRole.Optional).Select(e => e.OperationId)];
}

/// <summary>
/// The hardware an entry is for: each field that is set must hold (they combine with "and"). A field whose fact couldn't be read makes the answer
/// unknown, not no, so the entry is left alone rather than applied or hidden on a guess.
/// </summary>
/// <param name="Form">Any of the listed forms (<see cref="DeviceForm"/> names, lower case).</param>
/// <param name="Cpu"><c>intel</c>, <c>amd</c> or <c>hybrid</c>.</param>
/// <param name="Gpu"><c>any</c> (a hardware adapter), <c>discrete</c>, <c>nvidia</c>, <c>amd</c> or <c>intel</c>.</param>
/// <param name="Disk"><c>nvme</c>, <c>sata</c>, <c>ssd</c> or <c>hdd</c>: some drive of that kind is installed.</param>
/// <param name="Network"><c>wifi</c> or <c>ethernet</c>: a physical adapter of that kind.</param>
public sealed record HardwareCondition(IReadOnlyList<string>? Form = null, string? Cpu = null, string? Gpu = null, string? Disk = null, string? Network = null)
{
    public static readonly IReadOnlySet<string> CpuValues = new HashSet<string> { "intel", "amd", "hybrid" };
    public static readonly IReadOnlySet<string> GpuValues = new HashSet<string> { "any", "discrete", "nvidia", "amd", "intel" };
    public static readonly IReadOnlySet<string> DiskValues = new HashSet<string> { "nvme", "sata", "ssd", "hdd" };
    public static readonly IReadOnlySet<string> NetworkValues = new HashSet<string> { "wifi", "ethernet" };

    /// <summary>
    /// Whether this PC is what the entry is for: <c>true</c>, <c>false</c>, or <c>null</c> when a fact it depends on wasn't read. The first field that
    /// fails decides <paramref name="failed"/> (<c>disk.nvme</c>), so the app can say which hardware is missing.
    /// </summary>
    public bool? Holds(HardwareProfile hardware, out string? failed)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        failed = null;
        bool? result = true;
        foreach (var (field, answer) in Checks(hardware))
        {
            if (answer == false)
            {
                failed = field;
                return false;
            }

            if (answer is null)
            {
                failed ??= field;
                result = null;
            }
        }

        if (result is not null)
        {
            failed = null;
        }

        return result;
    }

    private IEnumerable<(string Field, bool? Answer)> Checks(HardwareProfile hardware)
    {
        if (Form is { Count: > 0 } forms)
        {
            yield return ($"form.{string.Join('|', forms)}", hardware.Form == DeviceForm.Unknown ? null : forms.Any(f => f.Equals(hardware.Form.ToString(), StringComparison.OrdinalIgnoreCase)));
        }

        if (Cpu is { } cpu)
        {
            yield return ($"cpu.{cpu}", cpu switch
            {
                "intel" => hardware.Cpu is null || hardware.Cpu.Maker == CpuMaker.Unknown ? null : hardware.Cpu.Maker == CpuMaker.Intel,
                "amd" => hardware.Cpu is null || hardware.Cpu.Maker == CpuMaker.Unknown ? null : hardware.Cpu.Maker == CpuMaker.Amd,
                _ => hardware.Cpu?.Hybrid,
            });
        }

        if (Gpu is { } gpu)
        {
            // An empty list is "no adapter read" more often than "no graphics at all": only a read adapter can answer yes, and "no" needs at least one adapter read.
            var gpus = hardware.Gpus;
            bool? answer = gpu switch
            {
                "any" => gpus.Count > 0 ? true : null,
                "discrete" => gpus.Any(g => g.IsDiscrete) ? true : gpus.Count > 0 && gpus.All(g => g.Integrated is not null) ? false : null,
                "nvidia" => gpus.Any(g => g.Maker == GpuMaker.Nvidia) ? true : gpus.Count > 0 ? false : null,
                "amd" => gpus.Any(g => g.Maker == GpuMaker.Amd) ? true : gpus.Count > 0 ? false : null,
                _ => gpus.Any(g => g.Maker == GpuMaker.Intel) ? true : gpus.Count > 0 ? false : null,
            };
            yield return ($"gpu.{gpu}", answer);
        }

        if (Disk is { } disk)
        {
            var disks = hardware.Disks;
            bool? answer = disks.Count == 0 ? null : disk switch
            {
                "nvme" => disks.Any(d => d.Class == StorageClass.Nvme),
                "sata" => disks.Any(d => d.Bus is Mesura.Core.Raw.BusKind.Sata or Mesura.Core.Raw.BusKind.Ata),
                "ssd" => disks.Any(d => d.Class is StorageClass.Ssd or StorageClass.Nvme),
                _ => disks.Any(d => d.Class == StorageClass.Hdd),
            };
            yield return ($"disk.{disk}", answer);
        }

        if (Network is { } network)
        {
            var has = network == "wifi" ? hardware.HasWifi : hardware.HasEthernet;
            yield return ($"network.{network}", has ? true : hardware.Network.Count > 0 ? false : (bool?)null);
        }
    }
}
