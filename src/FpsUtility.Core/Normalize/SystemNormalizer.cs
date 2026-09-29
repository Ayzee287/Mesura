using FpsUtility.Core.Facts;
using FpsUtility.Core.Raw;

namespace FpsUtility.Core.Normalize;

/// <summary>The operating system and power facts: direct observations, as the platform reported them.</summary>
internal static class SystemNormalizer
{
    public static OsFacts Os(OsInfo os) => new(
        Platform: Fact.Observed(os.Platform, Basis.Authoritative, "/os/platform"),
        Edition: Evidence.Observed(os.Edition, Basis.Authoritative, "/os/edition"),
        Version: Evidence.Observed(os.Version, Basis.Authoritative, "/os/version"),
        Build: Fact.Observed(os.Build, Basis.Authoritative, "/os/build"),
        UpdateRevision: Evidence.Observed(os.UpdateRevision, Basis.Authoritative, "/os/updateRevision"),
        Architecture: Evidence.Observed(os.Architecture, Basis.Authoritative, "/os/architecture"),
        ProductType: Evidence.Observed(os.ProductType, Basis.Authoritative, "/os/productType"),
        Session: Evidence.Observed(os.Session, Basis.Authoritative, "/os/session"));

    public static PowerFacts Power(SourceEntry<PowerData> source)
    {
        if (!Evidence.TryData(source, "power", out var power, out var unavailable))
        {
            return new PowerFacts(unavailable.Fact<bool>(), unavailable.Fact<bool>());
        }

        return new PowerFacts(
            OnAcPower: Evidence.Observed(power.AcPower, Basis.Authoritative, "/sources/power/data/acPower"),
            BatteryPresent: Evidence.Observed(power.BatteryPresent, Basis.Authoritative, "/sources/power/data/batteryPresent"));
    }
}
