using System.Threading.Tasks;
using Mesura.Core.Licensing;
using Mesura.Platform.Windows.Windows;
using Mesura.Setup.Windows.Licensing;

namespace Mesura.App;

/// <summary>A label and its value in a licence card's technical details.</summary>
public sealed record LicenseFact(string Label, string Value);

/// <summary>What a licence card's main button does.</summary>
public enum LicenseAction
{
    None,
    OpenSettings,
    EnterKey,
    Activate,
}

/// <summary>
/// One product's licence on Activation (simplified in the 2026-10-24 quality pass): its state in a word, one sentence,
/// one primary action, the last attempt's result when it failed, and the technical facts (licence, channel, renewal,
/// KMS host, firmware licence) behind a disclosure.
/// </summary>
public sealed class LicenseCard(LicensedProduct product, string title, string state, string tone, string line,
    LicenseAction primary, bool offersKey, IReadOnlyList<LicenseFact> facts, LicenseInfo? license, string problem, string problemDetail) : Observable
{
    private bool _expanded;

    public LicensedProduct Product { get; } = product;

    public string Glyph => System.Windows.Application.Current?.TryFindResource(Product == LicensedProduct.Windows ? "IconWindows" : "IconApps") as string ?? "";

    public string Title { get; } = title;

    /// <summary>Microsoft's own page about activating this product: the one outside link on the card.</summary>
    public string HelpUrl => Product == LicensedProduct.Windows
        ? "https://support.microsoft.com/en-us/windows/activate-windows-c39005d4-95ee-b91e-b399-2820fda32227"
        : "https://support.microsoft.com/en-us/office/activate-office-5bd38f38-db92-448b-a982-ad170b1e187e";

    public string State { get; } = state;

    public string Tone { get; } = tone;

    /// <summary>One plain sentence: where the licence stands and what, if anything, to do.</summary>
    public string Line { get; } = line;

    public LicenseAction Primary { get; } = primary;

    public string PrimaryLabel => Primary switch
    {
        LicenseAction.EnterKey => Loc.Get("Activation.EnterKey"),
        LicenseAction.Activate => Loc.Get("Activation.Activate"),
        LicenseAction.OpenSettings => Loc.Get("Activation.OpenSettings"),
        _ => "",
    };

    public bool HasPrimary => Primary != LicenseAction.None;

    /// <summary>Whether the primary action is the strong one (something to do), or a quiet way to Windows' page.</summary>
    public bool PrimaryIsStrong => Primary is LicenseAction.EnterKey or LicenseAction.Activate;

    public bool PrimaryIsQuiet => Primary == LicenseAction.OpenSettings;

    /// <summary>A product key can also be entered when the primary action is something else ("Use another key").</summary>
    public bool OffersKey { get; } = offersKey && primary != LicenseAction.EnterKey;

    public IReadOnlyList<LicenseFact> Facts { get; } = facts;

    public bool HasFacts => Facts.Count > 0 || ProblemDetail.Length > 0;

    /// <summary>The licence activation is asked for, or <c>null</c> when there is none to activate.</summary>
    public LicenseInfo? License { get; } = license;

    /// <summary>The last attempt's failure in words (empty when there was none), and its code for the details.</summary>
    public string Problem { get; } = problem;

    public string ProblemDetail { get; } = problemDetail;

    public bool HasProblem => Problem.Length > 0;

    public bool Expanded
    {
        get => _expanded;
        set => Set(ref _expanded, value);
    }
}

/// <summary>
/// Activation (D031, simplified 2026-10-24): two cards, Windows and Office, read without changing anything. Each shows its
/// state, one sentence and one primary action among Microsoft's own routes: open Settings > Activation, enter a product
/// key the user owns (installed and activated through Windows' licensing service, in this process), or activate an
/// installed licence online. Licence descriptions, channels, renewal periods, a KMS host and the firmware's licence are
/// in the technical details. No key is stored or logged; nothing sets a KMS host, uses an activator or rearms, and
/// Mesura runs no activation script.
/// </summary>
public sealed class ActivationViewModel : Observable, IPageState
{
    private readonly Services _services;
    private readonly Dictionary<LicensedProduct, (string Text, uint Code, string Detail)> _problems = [];
    private ActivationStatus? _status;
    private bool _busy;
    private string _keyFor = "";
    private string _keyError = "";

    public ActivationViewModel(Services services)
    {
        _services = services;
        Refresh = new AsyncCommand(LoadAsync, () => !_busy, Fail);
        OpenSettings = new RelayCommand(OpenActivationSettings);
        Primary = new AsyncCommand<LicenseCard>(PrimaryAsync, card => card is { HasPrimary: true } && (card.Primary == LicenseAction.OpenSettings || CanChange), Fail);
        EnterKey = new RelayCommand<LicenseCard>(card =>
        {
            KeyFor = card?.Product == LicensedProduct.Office ? "office" : "windows";
            KeyError = "";
        }, card => CanChange && card is not null);
        Help = new RelayCommand<LicenseCard>(card =>
        {
            if (card is not null && ShellLauncher.Open(card.HelpUrl) is { } error)
            {
                Result.Show(Mesura.App.Tone.Warn, Loc.Get("Firmware.CantOpenLink"), technical: error);
            }
        }, card => card is not null);
        CancelKey = new RelayCommand(() =>
        {
            KeyFor = "";
            KeyError = "";
        });
        services.ChangingChanged += () =>
        {
            // Another page started a run: the key field closes, so no key goes to Windows while that run changes the PC.
            if (_services.Changing && !_busy)
            {
                KeyFor = "";
            }

            RaiseCommands();
        };
        Loc.Instance.LanguageChanged += Build;
        Refresh.Execute(null);
    }

    public AsyncCommand Refresh { get; }

    public RelayCommand OpenSettings { get; }

    /// <summary>A card's main button: enter a key, activate, or open Windows' Activation page.</summary>
    public AsyncCommand<LicenseCard> Primary { get; }

    public RelayCommand<LicenseCard> EnterKey { get; }

    /// <summary>Opens Microsoft's page about activating the card's product.</summary>
    public RelayCommand<LicenseCard> Help { get; }

    public RelayCommand CancelKey { get; }

    public Banner Result { get; } = new();

    public LicenseCard? Windows { get; private set; }

    public LicenseCard? Office { get; private set; }

    public bool HasOffice => Office is not null;

    public bool Busy
    {
        get => _busy;
        private set
        {
            Set(ref _busy, value);
            RaiseCommands();
        }
    }

    public bool Elevated => _services.Reader.IsElevated;

    /// <summary>Whether a key or an activation can go to Windows now: nothing else running, and Mesura elevated.</summary>
    private bool CanChange => !_busy && !_services.Changing && _services.Reader.IsElevated;

    /// <summary>Said once under the title when a card's action needs administrator rights that Mesura doesn't have.</summary>
    public string AdminNote => !_services.Reader.IsElevated && (Windows?.PrimaryIsStrong == true || Office?.PrimaryIsStrong == true
        || Windows?.OffersKey == true || Office?.OffersKey == true || Windows?.License?.State is not (null or LicenseState.Licensed))
        ? Loc.Get(Loc.AdminKey("Activation.AdminHint"))
        : "";

    public string Status => _status is null ? Loc.Get("Activation.Checking")
        : _status.Error is not null ? Loc.Get("Activation.ReadFailed")
        : Loc.Get("Activation.Subtitle");

    /// <summary>Which product the key field is open for (<c>windows</c>, <c>office</c>), or empty.</summary>
    public string KeyFor
    {
        get => _keyFor;
        private set
        {
            Set(ref _keyFor, value);
            Raise(nameof(EnteringKey));
            Raise(nameof(EnteringWindowsKey));
            Raise(nameof(EnteringOfficeKey));
            Raise(nameof(KeyTitle));
        }
    }

    public bool EnteringKey => _keyFor.Length > 0;

    public bool EnteringWindowsKey => _keyFor == "windows";

    public bool EnteringOfficeKey => _keyFor == "office";

    /// <summary>Whether the key field's Install can run: nothing else running, and Mesura elevated.</summary>
    public bool CanInstallKey => CanChange;

    public string KeyTitle => _keyFor == "office" ? Loc.Get("Activation.KeyTitleOffice") : Loc.Get("Activation.KeyTitleWindows");

    public string KeyError
    {
        get => _keyError;
        private set => Set(ref _keyError, value);
    }

    public void Leave() => Result.Settle();

    private void OpenActivationSettings()
    {
        if (ShellLauncher.Open("ms-settings:activation") is { } error)
        {
            Result.Show(Mesura.App.Tone.Warn, error);
        }
    }

    private async Task LoadAsync()
    {
        Busy = true;
        try
        {
            _status = await Task.Run(LicensingReader.Read);
            if (_status.Error is { } error)
            {
                _services.Log($"Activation: the licences couldn't be read: {error}");
            }

            Build();
        }
        finally
        {
            Busy = false;
        }
    }

    private void Build()
    {
        Windows = _status is null ? null : WindowsCard(_status);
        Office = _status is null ? null : OfficeCard(_status);
        Raise(string.Empty);
        RaiseCommands();
    }

    private static string StateWord(LicenseInfo? license) => license?.State switch
    {
        LicenseState.Licensed => Loc.Get("Activation.State.Licensed"),
        LicenseState.OobGrace or LicenseState.OotGrace or LicenseState.ExtendedGrace => Loc.Get("Activation.State.Grace"),
        LicenseState.NonGenuineGrace => Loc.Get("Activation.State.NonGenuine"),
        LicenseState.Notification => Loc.Get("Activation.State.Notification"),
        LicenseState.Unlicensed => Loc.Get("Activation.State.Unlicensed"),
        null => Loc.Get("Activation.State.NoKey"),
        _ => Loc.Get("Activation.State.Unknown"),
    };

    private static string ToneOf(LicenseInfo? license) => license?.State switch
    {
        LicenseState.Licensed => Mesura.App.Tone.Ok,
        LicenseState.OobGrace or LicenseState.OotGrace or LicenseState.ExtendedGrace => Mesura.App.Tone.Warn,
        _ => Mesura.App.Tone.Danger,
    };

    /// <summary>The channel in words, with Windows' own name for it.</summary>
    private static string Channel(LicenseInfo license)
    {
        var words = license.IsKmsClient ? Loc.Get("Activation.Channel.Kms")
            : license.IsMak ? Loc.Get("Activation.Channel.Mak")
            : license.IsOem ? Loc.Get("Activation.Channel.Oem")
            : license.IsRetail ? Loc.Get("Activation.Channel.Retail")
            : "";
        return words.Length == 0 ? license.Channel : license.Channel.Length > 0 ? $"{words} ({license.Channel})" : words;
    }

    private static string Clean(string name) => name.Replace("(R)", "", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal).Trim();

    /// <summary>The technical facts of a licence, for the details.</summary>
    private static List<LicenseFact> Facts(LicenseInfo license)
    {
        var facts = new List<LicenseFact> { new(Loc.Get("Activation.Licence"), Clean(license.Name)) };
        if (Channel(license) is { Length: > 0 } channel)
        {
            facts.Add(new(Loc.Get("Activation.ChannelLabel"), channel));
        }

        if (license.GraceDays is { } days)
        {
            facts.Add(new(license.State == LicenseState.Licensed ? Loc.Get("Activation.Renewal") : Loc.Get("Activation.GraceLeft"),
                Loc.Plural("Activation.Days", days)));
        }

        if (license.KmsHost is { Length: > 0 } host)
        {
            facts.Add(new(Loc.Get("Activation.KmsHost"), host));
        }

        return facts;
    }

    /// <summary>
    /// The card's name: Windows and its major version, never the edition (0.4: "Windows Pro" headed the card and said more than anyone asked; the edition is in
    /// the technical details with the licence's own name).
    /// </summary>
    private static string WindowsTitle() => Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10";

    private (string Problem, string Detail, uint Code) ProblemOf(LicensedProduct product) =>
        _problems.TryGetValue(product, out var p) ? (p.Text, p.Detail, p.Code) : ("", "", 0u);

    private LicenseCard WindowsCard(ActivationStatus status)
    {
        var license = status.WindowsLicense;
        var facts = license is null ? [] : Facts(license);
        var firmware = status.FirmwareEdition switch
        {
            "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Windows Home",
            "Professional" => "Windows Pro",
            "" => "",
            var other => other,
        };
        if (firmware.Length > 0)
        {
            facts.Add(new(Loc.Get("Activation.Firmware"), Loc.Format("Activation.FirmwareValue", firmware)));
        }

        // The firmware's licence is for Home while Pro is installed: that licence can't activate this edition (details only).
        if (status.FirmwareEdition is "Core" or "CoreSingleLanguage" or "CoreCountrySpecific" && license is not null
            && license.Name.Contains("Professional", StringComparison.OrdinalIgnoreCase))
        {
            facts.Add(new(Loc.Get("Activation.Note"), Loc.Format("Activation.FirmwareDiffers", firmware)));
        }

        var licensed = license?.State == LicenseState.Licensed;
        var line = status.Error is not null ? Loc.Get("Activation.Line.Unreadable")
            : licensed ? Loc.Get("Activation.Line.WindowsActive")
            : license is null ? Loc.Get("Activation.Line.WindowsNoKey")
            : license.GraceDays is { } days ? Loc.Format("Activation.Line.WindowsGrace", Loc.Plural("Activation.Days", days))
            : Loc.Get("Activation.Line.WindowsInactive");
        // Activated: nothing to do, so the quiet way to Windows' page. Not activated: a key the user owns, which needs
        // Mesura elevated; without that, Windows' page (where the same is possible) is the action.
        var primary = licensed || status.Error is not null ? LicenseAction.OpenSettings
            : _services.Reader.IsElevated ? LicenseAction.EnterKey
            : LicenseAction.OpenSettings;
        var (problem, detail, _) = ProblemOf(LicensedProduct.Windows);
        return new LicenseCard(LicensedProduct.Windows, WindowsTitle(), StateWord(license), ToneOf(license), line,
            primary, offersKey: !licensed, facts, license, problem, detail);
    }

    private LicenseCard? OfficeCard(ActivationStatus status)
    {
        var install = status.OfficeInstall;
        var license = status.Office.FirstOrDefault(o => o.State != LicenseState.Licensed) ?? status.Office.FirstOrDefault();
        if (install is null && license is null)
        {
            return null;
        }

        var facts = license is null ? [] : Facts(license);
        if (install is not null)
        {
            facts.Insert(0, new(Loc.Get("Activation.OfficeEdition"), install.ProductReleaseIds + (install.Version.Length > 0 ? $" · {install.Version}" : "")));
        }

        if (license is { State: not LicenseState.Licensed } && (license.IsMak || license.IsKmsClient))
        {
            facts.Add(new(Loc.Get("Activation.Note"), Loc.Get("Activation.OfficeVolumeNote")));
        }

        var account = install is { ActivatesWithAccount: true } && license is null;
        var licensed = license?.State == LicenseState.Licensed;
        var line = account ? Loc.Get("Activation.OfficeAccount")
            : licensed ? Loc.Get("Activation.Line.OfficeActive")
            : license?.GraceDays is { } days ? Loc.Format("Activation.Line.OfficeGrace", Loc.Plural("Activation.Days", days))
            : Loc.Get("Activation.Line.OfficeInactive");
        var takesKey = install?.IsVolume == true || license is not null;
        var (problem, detail, code) = ProblemOf(LicensedProduct.Office);
        // An installed licence that isn't activated: activate it. When the last attempt failed because of the key itself,
        // the primary action becomes entering another key. A subscription activates by signing in to Office: nothing here.
        var primary = account || licensed ? LicenseAction.None
            : license is null ? (takesKey ? LicenseAction.EnterKey : LicenseAction.None)
            : code != 0 && ActivationErrors.IsKeyProblem(code) ? LicenseAction.EnterKey
            : LicenseAction.Activate;
        return new LicenseCard(LicensedProduct.Office, "Office", account ? Loc.Get("Activation.State.Account") : StateWord(license),
            account ? Mesura.App.Tone.Info : ToneOf(license), line, primary, takesKey && !licensed, facts, license, problem, detail);
    }

    private async Task PrimaryAsync(LicenseCard? card)
    {
        switch (card?.Primary)
        {
            case LicenseAction.OpenSettings:
                OpenActivationSettings();
                break;
            case LicenseAction.EnterKey:
                EnterKey.Execute(card);
                break;
            case LicenseAction.Activate:
                await ActivateAsync(card);
                break;
        }
    }

    /// <summary>
    /// Installs a product key the user typed for the product the field is open for, then activates it. Called by the page
    /// with the masked field's text, which the page clears straight after; the key is passed to Windows' licensing
    /// service in this process and kept nowhere.
    /// </summary>
    public async Task InstallKeyAsync(string typed)
    {
        if (!CanInstallKey)
        {
            return;
        }

        if (!ProductKey.TryNormalize(typed, out var key))
        {
            KeyError = Loc.Get("Activation.KeyInvalid");
            return;
        }

        var office = _keyFor == "office";
        var field = office ? "Office" : "Windows";
        if (!ConfirmDialog.Ask(Loc.Format("Activation.KeyConfirmTitle", field), Loc.Get("Activation.KeyConfirm"), confirm: Loc.Get("Activation.KeyInstall")))
        {
            return;
        }

        Busy = true;
        using var change = _services.BeginChange();
        try
        {
            // Windows gives a key to the product it belongs to, whichever field it was typed in: the licences before and
            // after the install tell which one received it.
            var current = await Task.Run(LicensingReader.Read);
            var before = current.Windows.Concat(current.Office).Select(l => l.SkuId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var installed = await Task.Run(() => LicensingWriter.InstallProductKey(key));
            key = "";
            _services.Log($"Activation: a product key was given to Windows' licensing service from the {field} field (the key isn't recorded): "
                + (installed.Ok ? "installed." : $"refused, {ActivationErrors.Hex(installed.Code)}."));
            if (!installed.Ok)
            {
                KeyFor = "";
                Note(office ? LicensedProduct.Office : LicensedProduct.Windows, Loc.Get("Activation.KeyRefused"), installed, kmsClient: false);
                Build();
                return;
            }

            KeyFor = "";
            _status = await Task.Run(LicensingReader.Read);
            var licences = _status.Windows.Concat(_status.Office).ToList();
            var target = licences.FirstOrDefault(l => !before.Contains(l.SkuId) && l.State != LicenseState.Licensed)
                ?? licences.FirstOrDefault(l => !before.Contains(l.SkuId))
                ?? (office ? _status.Office.FirstOrDefault(l => l.State != LicenseState.Licensed) : _status.WindowsLicense);
            var product = target?.Product ?? (office ? LicensedProduct.Office : LicensedProduct.Windows);
            if (product == LicensedProduct.Windows)
            {
                // Microsoft's advice after a retail key (slmgr /cpky): keep it out of the registry; it stays installed.
                var cleared = await Task.Run(LicensingWriter.ClearKeyFromRegistry);
                _services.Log($"Activation: the key was cleared from the registry: {(cleared.Ok ? "yes" : ActivationErrors.Hex(cleared.Code))}.");
            }

            _problems.Remove(product);
            Build();
            var name = product == LicensedProduct.Office ? "Office" : "Windows";
            if (target is { State: not LicenseState.Licensed })
            {
                await ActivateLicenseAsync(product, name, target);
            }
            else
            {
                Result.Show(Mesura.App.Tone.Ok, Loc.Format("Activation.KeyInstalled", name));
            }

            if (product != (office ? LicensedProduct.Office : LicensedProduct.Windows))
            {
                _services.Log($"Activation: the key belonged to {name}, not to {field}.");
                Result.Show(Mesura.App.Tone.Warn, Loc.Format("Activation.KeyOtherProduct", name, field),
                    string.Join("\n", new[] { Result.Title, Result.Detail }.Where(t => t.Length > 0)));
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ActivateAsync(LicenseCard card)
    {
        if (card.License is not { } license
            || !ConfirmDialog.Ask(Loc.Format("Activation.ActivateConfirmTitle", card.Title), Loc.Get("Activation.ActivateConfirm"), confirm: Loc.Get("Activation.Activate")))
        {
            return;
        }

        Busy = true;
        using var change = _services.BeginChange();
        try
        {
            await ActivateLicenseAsync(card.Product, card.Title, license);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task ActivateLicenseAsync(LicensedProduct product, string title, LicenseInfo license)
    {
        var result = await Task.Run(() => LicensingWriter.Activate(license.SkuId));
        _services.Log($"Activation: online activation of {title}: {(result.Ok ? "done" : ActivationErrors.Hex(result.Code))}.");
        _status = await Task.Run(LicensingReader.Read);
        if (result.Ok)
        {
            _problems.Remove(product);
            Build();
            Result.Show(Mesura.App.Tone.Ok, Loc.Format("Activation.Activated", title));
        }
        else
        {
            Note(product, Loc.Format("Activation.NotActivated", title), result, license.IsKmsClient);
            Build();
        }
    }

    /// <summary>
    /// Keeps a failed attempt on its card: the reason in words, the Windows error code in the details. The page's banner
    /// doesn't repeat it, so the card is where it is read.
    /// </summary>
    private void Note(LicensedProduct product, string title, LicensingResult result, bool kmsClient)
    {
        var reason = result.Code == 0 ? Loc.Get("Activation.Error.Other") : Loc.Get(ActivationErrors.Key(result.Code, kmsClient));
        _problems[product] = ($"{title}: {reason}", result.Code,
            result.Code == 0 ? result.Message : ActivationErrors.Hex(result.Code) + (result.Message.Length > 0 ? " · " + result.Message : ""));
        Result.Hide();
    }

    private void Fail(Exception e)
    {
        Busy = false;
        _services.Log($"Activation: {e}");
        Result.Show(Mesura.App.Tone.Danger, Loc.Get("Common.Unexpected"), technical: e.Message);
    }

    private void RaiseCommands()
    {
        Refresh.RaiseCanExecuteChanged();
        Primary.RaiseCanExecuteChanged();
        EnterKey.RaiseCanExecuteChanged();
        Raise(nameof(CanInstallKey));
        Raise(nameof(AdminNote));
    }
}
