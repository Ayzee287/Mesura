using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mesura.Core.Nvidia;
using Mesura.Core.Support;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>An NVIDIA driver package that was downloaded, checked and unpacked, ready to be planned against.</summary>
public sealed record PreparedDriverPackage(string Folder, string SetupExe, string Sha256, string SignerSubject, IReadOnlyList<string> Packages);

/// <summary>How a run of NVIDIA's installer ended: its exit code read as <see cref="OfficialInstall.Outcome"/>, the log it wrote, how long it took.</summary>
public sealed record OfficialRunResult(OfficialInstallOutcome Outcome, int ExitCode, string LogFile, TimeSpan Elapsed, string Detail);

/// <summary>
/// The setup layer's owner of the official NVIDIA installer route (architecture §2.3, 0.5): it checks the downloaded package's Authenticode signature and publisher,
/// unpacks it with Windows' own <c>tar</c> (the package is a 7-Zip self-extracting archive, which <c>tar</c> reads), reads the package names the unpacked
/// folders declare, and runs the package's own, unmodified <c>setup.exe</c> with the command line <see cref="OfficialInstall.Plan"/> built. It never edits the package,
/// never runs anything from a folder a program running as the user could have changed while Mesura is elevated, and checks <c>setup.exe</c>'s signature again right before
/// it starts. The one thing it does that changes the PC is that start, and every caller asks the user first.
/// </summary>
public sealed partial class NvidiaOfficialSetup
{
    private const string Publisher = "NVIDIA Corporation";

    /// <summary>Windows' own <c>tar.exe</c> (present from Windows 10 1803), or <c>null</c>.</summary>
    public static string? TarPath
    {
        get
        {
            var path = Path.Combine(Environment.SystemDirectory, "tar.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>The package's signature and publisher; an error to show when it is not NVIDIA's, validly signed file.</summary>
    public static string? CheckSignature(string file, out AuthenticodeResult result)
    {
        result = AuthenticodeFile.Verify(file);
        if (!result.Valid)
        {
            return $"{Path.GetFileName(file)} is not validly signed: {result.Detail}.";
        }

        return result.Subject.Contains($"O={Publisher}", StringComparison.OrdinalIgnoreCase) || result.Subject.Contains($"CN={Publisher}", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{Path.GetFileName(file)} is signed, but not by {Publisher} ({result.Subject}).";
    }

    /// <summary>
    /// Verifies and unpacks <paramref name="downloaded"/> into <paramref name="folder"/> (created here, must not exist yet) and reads its package names. The folder must be one
    /// that programs running as the user cannot change when this process is elevated.
    /// </summary>
    public PreparedDriverPackage Prepare(string downloaded, string folder, bool elevated, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloaded);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (CheckSignature(downloaded, out var signature) is { } refused)
        {
            throw new InvalidOperationException(refused);
        }

        if (Directory.Exists(folder))
        {
            throw new InvalidOperationException($"{folder} already exists; an unpack goes into a new folder.");
        }

        if (elevated && ProgramFolderTrust.Why(folder) is { } why)
        {
            throw new InvalidOperationException($"{folder} is in a folder that programs running as you can change ({why}), so the installer is not unpacked there with administrator rights.");
        }

        var tar = TarPath ?? throw new InvalidOperationException("Windows' tar.exe isn't available (Windows 10 1803 or later has it).");
#pragma warning disable RS0030 // The official NVIDIA route's owner class (class remarks): the new unpack folder, and unpacking with Windows' own tar.
        Directory.CreateDirectory(folder);
        using (var process = Process.Start(new ProcessStartInfo(tar) { ArgumentList = { "-xf", downloaded, "-C", folder }, UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("tar didn't start."))
        {
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The package couldn't be unpacked (tar exit code {process.ExitCode}).");
            }
        }
#pragma warning restore RS0030

        cancellationToken.ThrowIfCancellationRequested();
        var setup = Path.Combine(folder, "setup.exe");
        if (!File.Exists(setup))
        {
            throw new InvalidOperationException("The package has no setup.exe: it isn't an NVIDIA driver package.");
        }

        using var stream = File.OpenRead(downloaded);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new PreparedDriverPackage(folder, setup, hash, signature.Subject, PackageNames(folder));
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// Downloads NVIDIA's package from <paramref name="url"/> (an address NVIDIA's own lookup gave, on NVIDIA's download host) into <paramref name="folder"/>, checks its signature and
    /// publisher, and returns its path. A folder that programs running as the user can change is refused when this process is elevated, and the file is checked after it is written.
    /// </summary>
    public async Task<string> DownloadAsync(Uri url, string folder, bool elevated, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (url.Scheme != Uri.UriSchemeHttps || !url.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase) || !url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{url} is not an https address of an .exe on NVIDIA's download host; it is not downloaded.");
        }

#pragma warning disable RS0030 // The official NVIDIA route's owner class (class remarks): the folder and the downloaded package.
        Directory.CreateDirectory(folder);
        if (elevated && ProgramFolderTrust.Why(folder) is { } why)
        {
            throw new InvalidOperationException($"{folder} is in a folder that programs running as you can change ({why}), so the driver package is not kept there while Mesura has administrator rights.");
        }

        var target = Path.Combine(folder, Path.GetFileName(url.AbsolutePath));
        var part = target + ".part";
        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = File.Create(part);
            await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        File.Move(part, target, overwrite: true);
#pragma warning restore RS0030
        if (CheckSignature(target, out _) is { } refused)
        {
#pragma warning disable RS0030 // The official NVIDIA route's owner class: a package that failed its signature check is removed.
            File.Delete(target);
#pragma warning restore RS0030
            throw new InvalidOperationException(refused);
        }

        return target;
    }

    /// <summary>The package names the unpacked folders declare: each <c>.nvi</c> one folder down, and the sub-packages <c>setup.cfg</c> names.</summary>
    public static IReadOnlyList<string> PackageNames(string folder)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nvi in Directory.EnumerateFiles(folder, "*.nvi", SearchOption.AllDirectories).Where(f => Depth(folder, f) <= 2))
        {
            if (OfficialInstall.PackageName(File.ReadAllText(nvi)) is { } name)
            {
                names.Add(name);
            }
        }

        var config = Path.Combine(folder, "setup.cfg");
        if (File.Exists(config))
        {
            foreach (Match match in SubPackage().Matches(File.ReadAllText(config)))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return [.. names];
    }

    /// <summary>
    /// Runs the package's own <c>setup.exe</c> with <paramref name="plan"/>'s arguments and waits for it (up to <paramref name="timeout"/>; a timeout never kills the
    /// installer, which a half-finished driver install makes worse). Refuses a plan without the display driver, and any <c>setup.exe</c> whose signature or publisher
    /// has changed since <see cref="Prepare"/>.
    /// </summary>
    public OfficialRunResult Run(PreparedDriverPackage package, OfficialInstallPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.HasDisplayDriver || !plan.Install.Contains("Display.Driver", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The plan has no display driver, so nothing was started.");
        }

        if (CheckSignature(package.SetupExe, out _) is { } refused)
        {
            throw new InvalidOperationException(refused);
        }

        var log = plan.Arguments.Select(a => a.StartsWith("-log:", StringComparison.Ordinal) ? a[5..] : null).FirstOrDefault(a => a is not null) ?? "";
        var started = Stopwatch.StartNew();
#pragma warning disable RS0030 // The official NVIDIA route's owner class (class remarks): the package's own setup.exe, on the user's confirmed choice.
        var start = new ProcessStartInfo(package.SetupExe) { WorkingDirectory = package.Folder, UseShellExecute = false };
        foreach (var argument in plan.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("NVIDIA's setup.exe didn't start.");
#pragma warning restore RS0030
            var deadline = DateTime.UtcNow + timeout;
            while (!process.WaitForExit(500))
            {
                if (cancellationToken.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    return new OfficialRunResult(OfficialInstallOutcome.Failed, -1, log, started.Elapsed,
                        "Stopped waiting for NVIDIA's installer; it was left running (killing a driver install half way is worse). Check Windows before trying again.");
                }
            }

            var code = process.ExitCode;
            var outcome = OfficialInstall.Outcome(code);
            return new OfficialRunResult(outcome, code, log, started.Elapsed, outcome switch
            {
                OfficialInstallOutcome.Installed => "NVIDIA's installer finished (exit code 0).",
                OfficialInstallOutcome.InstalledRestartNeeded => "NVIDIA's installer finished and asks for a restart (exit code 1).",
                _ => string.Create(CultureInfo.InvariantCulture, $"NVIDIA's installer failed (exit code {code})."),
            });
        }
        catch (Win32Exception e)
        {
            return new OfficialRunResult(OfficialInstallOutcome.Failed, e.NativeErrorCode, log, started.Elapsed, $"NVIDIA's setup.exe didn't start: {e.Message}");
        }
    }

    private static int Depth(string root, string file) => Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Length - 1;

    [GeneratedRegex(@"<sub-package\s+name\s*=\s*""([^""$]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SubPackage();
}
