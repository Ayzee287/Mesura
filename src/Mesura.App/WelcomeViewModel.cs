using System.Windows.Input;

namespace Mesura.App;

/// <summary>One way in from the Welcome page: a page of the dock, its glyph, and (for the three main ones) a line saying what it is for.</summary>
public sealed class WelcomeDoor(string titleKey, string hintKey, string glyph, Action open) : Observable
{
    public string Title => Loc.Get(titleKey);

    public string Hint => hintKey.Length == 0 ? "" : Loc.Get(hintKey);

    public string Glyph { get; } = glyph;

    public ICommand Open { get; } = new RelayCommand(open);

    public void Refresh()
    {
        Raise(nameof(Title));
        Raise(nameof(Hint));
    }
}

/// <summary>
/// The Welcome page: what Mesura is in one line, what it promises, and the three places most people start; everything else of the dock is one
/// quiet line below. It shows no state of the PC and nothing that changes: the pages hold those.
/// </summary>
public sealed class WelcomeViewModel : Observable
{
    private readonly List<WelcomeDoor> _all = [];

    public WelcomeViewModel(Action toOptimize, Action toApps, Action toSettings, Action toDebloat, Action toFirmware, Action toSystem, Action toActivity)
    {
        Doors =
        [
            Add("Nav.Optimize", "Welcome.OptimizeHint", Resource("IconOptimize"), toOptimize),
            Add("Nav.Apps", "Welcome.AppsHint", Resource("IconApps"), toApps),
            Add("Optimize.AllSettings", "Welcome.SettingsHint", Resource("IconTweaks"), toSettings),
        ];
        Others =
        [
            Add("Nav.Debloat", "", Resource("IconDebloat"), toDebloat),
            Add("Nav.Firmware", "", Resource("IconBoard"), toFirmware),
            Add("Nav.SystemInfo", "", Resource("IconSystem"), toSystem),
            Add("Nav.Activity", "", Resource("IconActivity"), toActivity),
        ];
        Loc.Instance.LanguageChanged += () =>
        {
            foreach (var door in _all)
            {
                door.Refresh();
            }

            Raise(nameof(Promises));
        };
    }

    public IReadOnlyList<WelcomeDoor> Doors { get; }

    public IReadOnlyList<WelcomeDoor> Others { get; }

    public IReadOnlyList<string> Promises =>
    [
        Loc.Get("Welcome.PromisePreview"),
        Loc.Get("Welcome.PromiseUndo"),
        Loc.Get("Welcome.PromisePrivate"),
    ];

    private WelcomeDoor Add(string titleKey, string hintKey, string glyph, Action open)
    {
        var door = new WelcomeDoor(titleKey, hintKey, glyph, open);
        _all.Add(door);
        return door;
    }

    private static string Resource(string key) => (string)System.Windows.Application.Current.Resources[key];
}
