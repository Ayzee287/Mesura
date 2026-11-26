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
public sealed record ProfileEntry(string OperationId, string GroupId, EntryRole Role, string Why, bool SideEffects, HardwareCondition? When)
{
    /// <summary>
    /// The hardware restrictions of <c>profiles.json</c> that name this entry's operation (0.9, <see cref="HardwareRestriction"/>): each must hold too, besides
    /// <see cref="When"/>. Filled in when the profiles are read, so a profile's own entry and a restriction can't drift apart.
    /// </summary>
    public IReadOnlyList<HardwareRestriction> Restrictions { get; init; } = [];

    /// <summary>Every hardware condition the entry has to meet: its own <see cref="When"/>, then each restriction's, with the restriction's reason.</summary>
    public IReadOnlyList<(HardwareCondition When, HardwareRestriction? From)> Conditions =>
        [.. (When is null ? [] : new[] { (When, (HardwareRestriction?)null) }).Concat(Restrictions.Select(r => (r.When, (HardwareRestriction?)r)))];
}

/// <summary>
/// An owner's hardware policy over operations (0.9, <c>profiles.json</c> "restrictions"): the listed operations are applied by a level, or from a saved PC
/// setup, only on a PC that meets <paramref name="When"/>. Unlike an entry's <c>when</c>, it also holds for an operation no level lists (Game Bar removal) and
/// for operations a saved setup names one by one or through a collection. The first is the dual-CCD Ryzen X3D policy of vault D054 item 4.
/// </summary>
/// <param name="Id">A stable id for the policy (<c>dual-ccd-x3d</c>).</param>
/// <param name="Why">Why the operations are left out where the condition fails, in a sentence (the plan's detail).</param>
public sealed record HardwareRestriction(string Id, HardwareCondition When, IReadOnlyList<string> Operations, string Why)
{
    public bool Covers(string operationId) => Operations.Contains(operationId, StringComparer.OrdinalIgnoreCase);
}

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
/// <param name="Cpu">
/// <c>intel</c>, <c>amd</c>, <c>hybrid</c>, or (0.8) <c>x3d-dual-ccd</c> / <c>not-x3d-dual-ccd</c>: a Ryzen X3D with two core dies, or any other processor
/// (<see cref="CpuProfile.DualCcdX3d"/>); the second lets an entry stay off where AMD's driver parks a die for games.
/// </param>
/// <param name="Gpu"><c>any</c> (a hardware adapter), <c>discrete</c>, <c>nvidia</c>, <c>amd</c> or <c>intel</c>.</param>
/// <param name="Disk"><c>nvme</c>, <c>sata</c>, <c>ssd</c> or <c>hdd</c>: some drive of that kind is installed.</param>
/// <param name="Network"><c>wifi</c> or <c>ethernet</c>: a physical adapter of that kind.</param>
public sealed record HardwareCondition(IReadOnlyList<string>? Form = null, string? Cpu = null, string? Gpu = null, string? Disk = null, string? Network = null)
{
    public static readonly IReadOnlySet<string> CpuValues = new HashSet<string> { "intel", "amd", "hybrid", "x3d-dual-ccd", "not-x3d-dual-ccd" };
    public static readonly IReadOnlySet<string> GpuValues = new HashSet<string> { "any", "discrete", "nvidia", "amd", "intel" };
    public static readonly IReadOnlySet<string> DiskValues = new HashSet<string> { "nvme", "sata", "ssd", "hdd" };
    public static readonly IReadOnlySet<string> NetworkValues = new HashSet<string> { "wifi", "ethernet" };

    /// <summary>
    /// Processor conditions no PC meets together. A dual-CCD X3D is an AMD part by definition (<see cref="CpuTopology.DualCcdX3d"/> answers no for an Intel
    /// processor), so it excludes <c>intel</c> as well as its own negation. <c>hybrid</c> excludes nothing: it is a property of the cores, not of the maker.
    /// </summary>
    private static readonly (string A, string B)[] ExclusiveCpu = [("intel", "amd"), ("x3d-dual-ccd", "not-x3d-dual-ccd"), ("x3d-dual-ccd", "intel")];

    /// <summary>
    /// Whether no PC can meet both conditions (0.9), so two entries under them never apply in one plan: their <see cref="Form"/> lists share no form, or
    /// their <see cref="Cpu"/> values exclude each other. A missing condition holds anywhere, so it is never disjoint from anything. Graphics, drives and
    /// network adapters exclude nothing, because one PC can have several kinds at once. Used to check that a profile's conflicting entries are kept apart
    /// by their conditions (<see cref="LevelPlanning.ConditionAwareConflicts"/>).
    /// </summary>
    public static bool Disjoint(HardwareCondition? a, HardwareCondition? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (a.Form is { Count: > 0 } formsA && b.Form is { Count: > 0 } formsB && !formsA.Intersect(formsB, StringComparer.OrdinalIgnoreCase).Any())
        {
            return true;
        }

        return a.Cpu is { } cpuA && b.Cpu is { } cpuB
            && ExclusiveCpu.Any(x => (x.A == cpuA && x.B == cpuB) || (x.A == cpuB && x.B == cpuA));
    }

    /// <summary>Whether no PC can meet every condition of both lists (each list must hold whole): some condition of one excludes some condition of the other.</summary>
    public static bool Disjoint(IEnumerable<HardwareCondition> a, IEnumerable<HardwareCondition> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var right = b.ToList();
        return a.Any(x => right.Any(y => Disjoint(x, y)));
    }

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
                "x3d-dual-ccd" => hardware.Cpu?.DualCcdX3d,
                "not-x3d-dual-ccd" => !hardware.Cpu?.DualCcdX3d,
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
