namespace Mesura.Core.Support;

/// <summary>
/// Work that is built and tested but not part of the released product (0.5.5: the automated NVIDIA driver step and the network adapter tuning collection, both held for the next update). It is
/// off for everyone; a developer switches a feature on with <c>MESURA_EXPERIMENTAL=nvidia-driver-automation,network-adapter-tuning</c> (or <c>all</c>). Nothing in the app offers the switch.
/// </summary>
public static class Experimental
{
    public const string NvidiaDriverAutomation = "nvidia-driver-automation";

    public const string NetworkAdapterTuning = "network-adapter-tuning";

    public static bool Enabled(string feature)
    {
        var list = Environment.GetEnvironmentVariable("MESURA_EXPERIMENTAL");
        if (string.IsNullOrWhiteSpace(list))
        {
            return false;
        }

        var named = list.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return named.Contains("all", StringComparer.OrdinalIgnoreCase) || named.Contains(feature, StringComparer.OrdinalIgnoreCase);
    }
}
