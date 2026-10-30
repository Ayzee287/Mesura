namespace Mesura.Core.Update;

public enum UpdateOutcome
{
    /// <summary>Nothing newer is offered on this channel.</summary>
    UpToDate,

    /// <summary>A newer version is offered and may be shown.</summary>
    Available,

    /// <summary>A newer version is offered, but the user skipped that version.</summary>
    Skipped,

    /// <summary>A newer version is offered, but the user asked to be reminded later and that time hasn't come.</summary>
    Postponed,

    /// <summary>The manifest isn't to be believed (see <see cref="UpdateCheck.Reason"/>); nothing is offered.</summary>
    Rejected,
}

/// <summary>What the user told Mesura about updates so far (kept in the app's settings).</summary>
/// <param name="SkippedVersion">A version the user chose to skip; a still newer one is offered again.</param>
/// <param name="RemindAfterUtc">"Later": no reminder before this time.</param>
/// <param name="HighestSequence">The highest manifest sequence ever accepted: an older manifest is a replay and is refused.</param>
public sealed record UpdateState(string? SkippedVersion = null, DateTimeOffset? RemindAfterUtc = null, long HighestSequence = 0);

/// <summary>The result of judging a fetched manifest: what to show, or why nothing is shown.</summary>
public sealed record UpdateCheck(UpdateOutcome Outcome, string Reason, UpdateManifest? Manifest, UpdatePackage? Package)
{
    public static UpdateCheck Rejected(string reason) => new(UpdateOutcome.Rejected, reason, null, null);
}

/// <summary>
/// Decides what a downloaded manifest means for this installation. Everything that can make a manifest untrustworthy is checked
/// before anything in it is acted on: the signature first, then the channel it claims, its schema, its expiry, its sequence,
/// the version it offers and whether a package for this PC's architecture exists (docs/updates.md).
/// </summary>
public static class UpdatePolicy
{
    public static UpdateCheck Evaluate(
        byte[] manifestBytes,
        string signature,
        IEnumerable<string> trustedKeys,
        string installedVersion,
        string channel,
        string architecture,
        DateTimeOffset now,
        UpdateState state,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(state);

        if (!UpdateSignature.Verify(manifestBytes, signature, trustedKeys))
        {
            return UpdateCheck.Rejected("signature");
        }

        if (UpdateManifest.TryParse(manifestBytes) is not { } manifest)
        {
            return UpdateCheck.Rejected("unreadable");
        }

        if (manifest.SchemaVersion != UpdateManifest.CurrentSchema)
        {
            return UpdateCheck.Rejected("schema"); // a newer format than this app understands: it must be updated by hand once
        }

        if (!string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheck.Rejected("channel");
        }

        if (manifest.ExpiresUtc <= now)
        {
            return UpdateCheck.Rejected("expired");
        }

        if (manifest.Sequence < state.HighestSequence)
        {
            return UpdateCheck.Rejected("replay");
        }

        var offered = AppVersion.TryParse(manifest.Version);
        var installed = AppVersion.TryParse(installedVersion);
        if (offered is null || installed is null)
        {
            return UpdateCheck.Rejected("version");
        }

        if (offered.IsPrerelease && string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheck.Rejected("prerelease-on-stable");
        }

        if (offered <= installed)
        {
            return new UpdateCheck(UpdateOutcome.UpToDate, "", manifest, null);
        }

        if (manifest.PackageFor(architecture, language) is not { } package)
        {
            return UpdateCheck.Rejected("no-package");
        }

        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var url) || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
            || package.Sha256.Length != 64 || !package.Sha256.All(Uri.IsHexDigit) || package.Size <= 0)
        {
            return UpdateCheck.Rejected("package-description");
        }

        if (state.SkippedVersion is { } skipped && AppVersion.TryParse(skipped) is { } skippedVersion && offered <= skippedVersion)
        {
            return new UpdateCheck(UpdateOutcome.Skipped, "", manifest, package);
        }

        if (state.RemindAfterUtc is { } remind && remind > now)
        {
            return new UpdateCheck(UpdateOutcome.Postponed, "", manifest, package);
        }

        return new UpdateCheck(UpdateOutcome.Available, "", manifest, package);
    }
}
