using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Mesura.App;

/// <summary>
/// Makes a result line a live region: when a page shows a new result (an update, an install, an applied setting, a failure), assistive technology says it.
/// Without this a Narrator user who pressed Apply hears nothing and has to go looking for the result. Set <see cref="SerialProperty"/> on the result's title
/// text, bound to a number that changes with every result (the same sentence twice must be announced twice); the text itself is the element's accessible name.
/// The element is marked as a polite live region and a <c>LiveRegionChanged</c> event is raised once the new text is in place.
/// </summary>
public static class Announce
{
    public static readonly DependencyProperty SerialProperty =
        DependencyProperty.RegisterAttached("Serial", typeof(int), typeof(Announce), new PropertyMetadata(0, OnSerialChanged));

    public static int GetSerial(DependencyObject d) => (int)d.GetValue(SerialProperty);

    public static void SetSerial(DependencyObject d, int value) => d.SetValue(SerialProperty, value);

    private static void OnSerialChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text || (int)e.NewValue == 0)
        {
            return;
        }

        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);

        // After the bindings that carry the new text have run.
        text.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!text.IsVisible || string.IsNullOrWhiteSpace(AutomationProperties.GetName(text)))
            {
                return; // a result that was just cleared, or isn't on screen: nothing to say
            }

            var peer = UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }
}
