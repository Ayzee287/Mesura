using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Checks;

/// <summary>What kind of display a connection makes an output, for the checks (architecture §4.6). Product policy, not a fact.</summary>
internal enum DisplayKind
{
    /// <summary>A streamed or virtual target (indirect virtual, Miracast). Checks about a physical monitor don't apply.</summary>
    Virtual,

    /// <summary>A built-in panel.</summary>
    Internal,

    /// <summary>
    /// Any other connection. Not proof of a physical monitor: a virtual display driver chooses its own connection
    /// type, and Microsoft's own sample reports HDMI.
    /// </summary>
    External,

    /// <summary>The platform reported "other" or a code the collector doesn't name.</summary>
    Unrecognised,
}

/// <summary>The machine's form factor from its declared chassis type (DSP0134 §7.4.1), for the checks that depend on it.</summary>
internal enum FormFactor
{
    /// <summary>A desktop case: the class the motherboard-port question is about.</summary>
    FullDesktop,

    /// <summary>A small or all-in-one case, often an OEM build with hybrid wiring (§6.3): the checks apply, at most Likely.</summary>
    Compact,

    /// <summary>A laptop-class device: hybrid graphics wiring is by design.</summary>
    Portable,

    /// <summary>Other, Unknown, any other defined type (docking station, server, embedded), or not declared.</summary>
    Unknown,
}

internal static class Policies
{
    // Each switch lists every member without a discard: a new ConnectionKind or ChassisType fails the build (CS8509)
    // until someone decides what it means here. Unnamed values can't occur (the readers reject them).
#pragma warning disable CS8524
    public static DisplayKind KindOf(ConnectionKind connection) => connection switch
    {
        ConnectionKind.IndirectVirtual or ConnectionKind.Miracast => DisplayKind.Virtual,
        ConnectionKind.Internal or ConnectionKind.DisplayPortEmbedded or ConnectionKind.UdiEmbedded or ConnectionKind.Lvds => DisplayKind.Internal,
        ConnectionKind.Other => DisplayKind.Unrecognised,
        ConnectionKind.Vga or ConnectionKind.SVideo or ConnectionKind.CompositeVideo or ConnectionKind.ComponentVideo or ConnectionKind.Dvi
            or ConnectionKind.Hdmi or ConnectionKind.DJpn or ConnectionKind.Sdi or ConnectionKind.DisplayPort or ConnectionKind.Udi
            or ConnectionKind.SdtvDongle or ConnectionKind.IndirectWired or ConnectionKind.DisplayPortUsbTunnel => DisplayKind.External,
    };

    public static FormFactor FormFactorOf(ChassisType chassis) => chassis switch
    {
        ChassisType.Desktop or ChassisType.LowProfileDesktop or ChassisType.PizzaBox or ChassisType.MiniTower or ChassisType.Tower => FormFactor.FullDesktop,
        ChassisType.MiniPc or ChassisType.SealedCasePc or ChassisType.SpaceSaving or ChassisType.LunchBox or ChassisType.AllInOne => FormFactor.Compact,
        ChassisType.Portable or ChassisType.Laptop or ChassisType.Notebook or ChassisType.HandHeld or ChassisType.SubNotebook
            or ChassisType.Tablet or ChassisType.Convertible or ChassisType.Detachable => FormFactor.Portable,
        ChassisType.Other or ChassisType.Unknown or ChassisType.DockingStation or ChassisType.MainServerChassis or ChassisType.ExpansionChassis
            or ChassisType.SubChassis or ChassisType.BusExpansionChassis or ChassisType.PeripheralChassis or ChassisType.RaidChassis
            or ChassisType.RackMountChassis or ChassisType.MultiSystem or ChassisType.CompactPci or ChassisType.AdvancedTca or ChassisType.Blade
            or ChassisType.BladeEnclosure or ChassisType.IotGateway or ChassisType.EmbeddedPc or ChassisType.StickPc => FormFactor.Unknown,
    };
#pragma warning restore CS8524

    /// <summary>A missing chassis fact is an unknown form factor, never a guess.</summary>
    public static FormFactor FormFactorOf(Fact<ChassisType> chassis) =>
        chassis.IsKnown ? FormFactorOf(chassis.Value) : FormFactor.Unknown;
}
