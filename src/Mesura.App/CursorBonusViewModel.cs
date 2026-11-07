using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mesura.Core.Bonus;
using Mesura.Setup.Windows.Bonus;

namespace Mesura.App;

/// <summary>
/// The bonus card on Configurations (0.7): a free cursor pack Mesura carries, offered on its own and never part of a profile. Applying asks first and says what changes;
/// <see cref="Restore"/> puts the user's own cursors back. The work is <see cref="CursorBonus"/> (files, registry, read-back, saved cursors); this class only words it.
/// </summary>
public sealed class CursorBonusViewModel : Observable
{
    /// <summary>The cursors drawn on the card: the arrow, the link hand, the text beam, help and precision.</summary>
    private static readonly string[] PreviewFiles = ["pointer.cur", "link.cur", "beam.cur", "help.cur", "precision.cur"];

    private readonly Services _services;
    private CursorBonusState _state;
    private bool _canRestore;
    private IReadOnlyList<ImageSource>? _previews;

    public CursorBonusViewModel(Services services)
    {
        _services = services;
        Apply = new RelayCommand(DoApply, () => CanAct && _state != CursorBonusState.Applied);
        Restore = new RelayCommand(DoRestore, () => CanAct && _canRestore);
        ReadState();
        services.ChangingChanged += RaiseCommands;
        Loc.Instance.LanguageChanged += () => Raise(string.Empty);
    }

    public Banner Result { get; } = new();

    public RelayCommand Apply { get; }

    public RelayCommand Restore { get; }

    /// <summary>Whether the cursors can be changed now: nothing else is changing the PC and Mesura runs as the signed-in user.</summary>
    private bool CanAct => !_services.Changing && _services.SameUser && _services.Cursors.BlockedReason is null;

    public string Name => Loc.Get("Bonus.Cursors.Name");

    public string About => Loc.Format("Bonus.Cursors.About", CursorPack.Author);

    public string Credit => Loc.Format("Bonus.Cursors.Credit", CursorPack.SchemeName);

    public string StateText => Loc.Get(_state switch
    {
        CursorBonusState.Applied => "Bonus.Cursors.State.Applied",
        CursorBonusState.Changed => "Bonus.Cursors.State.Changed",
        _ => "Bonus.Cursors.State.NotApplied",
    });

    public string StateTone => _state switch
    {
        CursorBonusState.Applied => Tone.Ok,
        CursorBonusState.Changed => Tone.Warn,
        _ => Tone.Muted,
    };

    public bool ShowRestore => _canRestore;

    /// <summary>A few of the pack's cursors, decoded from the copy inside Mesura (nothing is written to show them).</summary>
    public IReadOnlyList<ImageSource> Previews => _previews ??= LoadPreviews();

    private static List<ImageSource> LoadPreviews()
    {
        var images = new List<ImageSource>();
        foreach (var name in PreviewFiles)
        {
            try
            {
                using var stream = CursorFiles.OpenEmbedded(name);
                if (stream is null)
                {
                    continue;
                }

                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                images.Add(frame);
            }
            catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or FileFormatException)
            {
                // A preview is decoration: a cursor that can't be drawn is simply not drawn.
            }
        }

        return images;
    }

    private void ReadState()
    {
        _state = _services.Cursors.State();
        _canRestore = _services.Cursors.CanRestore;
        Raise(nameof(StateText));
        Raise(nameof(StateTone));
        Raise(nameof(ShowRestore));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        Apply.RaiseCanExecuteChanged();
        Restore.RaiseCanExecuteChanged();
    }

    private void DoApply()
    {
        if (!ConfirmDialog.Ask(Loc.Get("Bonus.Cursors.ConfirmTitle"), Loc.Format("Bonus.Cursors.ConfirmText", CursorPack.Files.Count),
                [Loc.Get("Bonus.Cursors.ConfirmOnly"), Loc.Get("Bonus.Cursors.ConfirmBack")], confirm: Loc.Get("Bonus.Cursors.Apply")))
        {
            return;
        }

        Run(() => _services.Cursors.Apply(), "applied", applied: true);
    }

    private void DoRestore() => Run(() => _services.Cursors.Restore(), "put back", applied: false);

    private void Run(Func<CursorBonusResult> action, string log, bool applied)
    {
        try
        {
            var result = action();
            _services.Log($"Cursor bonus {log}: {result.Detail}");
            var detail = applied ? Loc.Get(result.Reloaded || !result.Changed ? "Bonus.Cursors.AppliedHint" : "Bonus.Cursors.AppliedNextSignIn") : "";
            Result.Show(Tone.Ok, Loc.Get(applied ? (result.Changed ? "Bonus.Cursors.Applied" : "Bonus.Cursors.AlreadyApplied") : "Bonus.Cursors.Restored"), detail,
                technical: result.Detail);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _services.Log($"Cursor bonus failed: {e.Message}");
            Result.Show(Tone.Danger, Loc.Get("Bonus.Cursors.Failed"), Loc.Get("Bonus.Cursors.FailedHint"), technical: e.Message);
        }

        ReadState();
    }

    /// <summary>Reads the state again (the page was shown, or a run was recorded).</summary>
    public void Refresh() => ReadState();
}
