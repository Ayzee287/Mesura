namespace FpsUtility.Core.Raw;

/// <summary>The <c>power</c> section (architecture §4.4).</summary>
/// <param name="AcPower">Whether the machine runs on AC power. Native: the platform's AC-line status code.</param>
/// <param name="BatteryPresent">Whether the platform reports a system battery. Native: the platform's battery flags.</param>
public sealed record PowerData(Reading<bool> AcPower, Reading<bool> BatteryPresent);
