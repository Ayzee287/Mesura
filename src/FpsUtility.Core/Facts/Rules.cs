namespace FpsUtility.Core.Facts;

/// <summary>
/// The derivation rules a derived fact names in <see cref="Fact{T}.Rule"/>. Each id means one fixed derivation,
/// described here; changing what a rule does means a new id and a facts schema bump (architecture §4.4
/// "As built in Milestone 2"). A fact without a rule is a direct observation.
/// </summary>
public static class Rules
{
    /// <summary>A display output's GPU: the adapter whose opaque id equals the output's, within one snapshot (§5 joins).</summary>
    public const string DisplayGpu = "display.gpu-by-adapter-id";

    /// <summary>
    /// A display output's monitor identity: the EDID whose monitor device path equals the output's, read from its
    /// base block (E-EDID 1.4: manufacturer ID bytes 8–9, three 5-bit letters; product code bytes 10–11, little-endian),
    /// only when that block's checksum was valid as stored.
    /// </summary>
    public const string DisplayMonitor = "display.monitor-from-edid";

    /// <summary>
    /// A display output's shared source: the other active outputs whose adapter id and source id both equal the
    /// output's, within one snapshot (a source id is scoped to its adapter). None is a known empty list. It is
    /// <see cref="FactGap.Implausible"/> when the output's own adapter or source id is empty, and
    /// <see cref="FactGap.Ambiguous"/> when another output could show this source unseen (its adapter id is empty, or its
    /// source id is empty on the same adapter), or when an adapter and target id pair among the outputs that would share
    /// it is listed twice (one output or two?).
    /// </summary>
    public const string DisplaySharedSource = "display.shared-source";

    /// <summary>
    /// A display output's offered modes: the platform's list less entries that aren't modes, a refresh of 0 or 1 Hz
    /// (what platforms use for "the hardware's default") or a width or height that isn't positive.
    /// </summary>
    public const string DisplayModes = "display.offered-modes";

    /// <summary>
    /// A PCI identity taken from the combined hardware ID when the platform gives no field-by-field one: vendor,
    /// device and revision only. The combined subsystem value is not split, because its layout isn't documented.
    /// </summary>
    public const string PciIdentityFromHardwareId = "pci.identity-from-hardware-id";

    /// <summary>
    /// A GPU's PCI device: the one PCI device of display class, or of unreported class, whose vendor, device and
    /// revision (and subsystem, where both sides report it) equal the adapter's. None is <see cref="FactGap.Unmatched"/>.
    /// It is <see cref="FactGap.Ambiguous"/> when several match (two identical cards), when such a device's own
    /// identity couldn't be read (it might be the one), or when another adapter matches the same device.
    /// </summary>
    public const string GpuPciDevice = "gpu.pci-device-by-identity";

    /// <summary>
    /// A physical disk's partitions. The physical disk is matched to the one OS disk that isn't virtual (bus type
    /// neither Storage Spaces nor file-backed) and whose number equals the physical disk's device id (documented
    /// for such disks). Where both report a location, the locations must be equal too. The partitions are those
    /// that name that OS disk's number.
    /// </summary>
    public const string DiskPartitions = "storage.disk-partitions";

    /// <summary>A partition's volume: the one volume whose opaque path is among the partition's access paths.</summary>
    public const string PartitionVolume = "storage.partition-volume";

    /// <summary>A volume's mount points: its partition's access paths, less the volume's own opaque path.</summary>
    public const string VolumeMountPoints = "storage.volume-mount-points";

    /// <summary>A volume's disk: <see cref="PartitionVolume"/> read backwards, then <see cref="DiskPartitions"/>.</summary>
    public const string VolumeDisk = "storage.volume-disk";

    /// <summary>
    /// Whether Steam is registered for the user: registered when the Steam source read Steam's install path;
    /// not registered when the source reports the registration absent (<c>notPresent</c>). Any other failure
    /// leaves the fact missing.
    /// </summary>
    public const string SteamRegistration = "steam.registration";

    /// <summary>An installed app, read from its <c>appmanifest_*.acf</c> (Valve KeyValues): <c>AppState</c>'s <c>appid</c>, <c>name</c>, <c>installdir</c>, <c>SizeOnDisk</c>.</summary>
    public const string SteamAppManifest = "steam.app-manifest";

    /// <summary>SMBIOS type 1 (DSP0134 §7.2): manufacturer (string at 04h), product name (05h).</summary>
    public const string SmbiosSystem = "smbios.type1";

    /// <summary>SMBIOS type 2 (§7.3): manufacturer (04h), product (05h).</summary>
    public const string SmbiosBoard = "smbios.type2";

    /// <summary>SMBIOS type 3 (§7.4): chassis type (05h bits 6:0; §7.4.1).</summary>
    public const string SmbiosChassis = "smbios.type3";

    /// <summary>
    /// SMBIOS type 4 (§7.5): socket (04h), manufacturer (07h), version (10h), max speed (14h, MHz, 0 unknown),
    /// status (18h: bit 6 populated), core count (23h, or 2Ah when 23h is FFh), cores enabled (24h / 2Ch),
    /// thread count (25h / 2Eh). A count of 0 is unknown.
    /// </summary>
    public const string SmbiosProcessor = "smbios.type4";

    /// <summary>
    /// SMBIOS type 17 (§7.18): size (0Ch: 0 no module, FFFFh unknown, bit 15 set for KiB units, 7FFFh for the
    /// extended size at 1Ch in MiB, which DSP0134 uses only from 32767 MiB; a populated size of 0, or an extended
    /// size below that, is implausible), form factor (0Eh), locators (10h, 11h), type (12h), speed (15h, MT/s, or
    /// the extended speed at 54h when 15h is FFFFh; 0 unknown), manufacturer (17h), part number (1Ah), configured
    /// speed (20h, or 58h when FFFFh). A table that can't be walked to its end gives no lists.
    /// </summary>
    public const string SmbiosMemoryDevice = "smbios.type17";
}
