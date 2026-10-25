using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mesura.App;

/// <summary>
/// The one icon registry: the apps' own icons, shipped inside Mesura (<c>Assets\Apps\&lt;id&gt;.png</c>, 96 px; sources in
/// that folder's README), so every page shows them offline and before the app is installed. Ids are the app catalog's
/// (Configurations' settings ports use the same ids). Pages don't call this directly: they place an <see cref="AppGlyph"/>.
/// </summary>
public static class AppIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon for an app id, or <c>null</c> when Mesura ships none for it.</summary>
    public static ImageSource? For(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return null;
        }

        if (Cache.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            if (Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Apps/{appId}.png", UriKind.Absolute)) is { } resource)
            {
                using var stream = resource.Stream;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException)
        {
            image = null; // no icon for this app: the glyph shows its initial
        }

        Cache[appId] = image;
        return image;
    }
}

/// <summary>
/// How an app is pictured everywhere in Mesura (Apps, Setup, Configurations, Profiles, Activity): its packaged icon, or,
/// when Mesura ships none, the same neutral tile with the app's initial. Set <see cref="AppId"/> and <see cref="Label"/>
/// (the app's name, used for the initial and read by screen readers); the size comes from Width and Height, the inner
/// space from Padding. The look is the implicit style in Templates.xaml.
/// </summary>
public sealed class AppGlyph : Control
{
    public static readonly DependencyProperty AppIdProperty = DependencyProperty.Register(
        nameof(AppId), typeof(string), typeof(AppGlyph), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(AppGlyph), new PropertyMetadata(null, OnChanged));

    private static readonly DependencyPropertyKey IconKey = DependencyProperty.RegisterReadOnly(
        nameof(Icon), typeof(ImageSource), typeof(AppGlyph), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey InitialKey = DependencyProperty.RegisterReadOnly(
        nameof(Initial), typeof(string), typeof(AppGlyph), new PropertyMetadata(""));

    private static readonly DependencyPropertyKey HasIconKey = DependencyProperty.RegisterReadOnly(
        nameof(HasIcon), typeof(bool), typeof(AppGlyph), new PropertyMetadata(false));

    public static readonly DependencyProperty IconProperty = IconKey.DependencyProperty;

    public static readonly DependencyProperty InitialProperty = InitialKey.DependencyProperty;

    public static readonly DependencyProperty HasIconProperty = HasIconKey.DependencyProperty;

    public AppGlyph()
    {
        Focusable = false;
        IsTabStop = false;
    }

    /// <summary>The app's catalog id (or a settings port's id, which is the same).</summary>
    public string? AppId
    {
        get => (string?)GetValue(AppIdProperty);
        set => SetValue(AppIdProperty, value);
    }

    /// <summary>The app's name.</summary>
    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public ImageSource? Icon => (ImageSource?)GetValue(IconProperty);

    public string Initial => (string)GetValue(InitialProperty);

    public bool HasIcon => (bool)GetValue(HasIconProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var glyph = (AppGlyph)d;
        var icon = AppIcons.For(glyph.AppId);
        glyph.SetValue(IconKey, icon);
        glyph.SetValue(HasIconKey, icon is not null);
        var name = glyph.Label is { Length: > 0 } label ? label : glyph.AppId ?? "";
        glyph.SetValue(InitialKey, name.Length > 0 ? name[..1].ToUpperInvariant() : "");
        System.Windows.Automation.AutomationProperties.SetName(glyph, name);
    }
}
