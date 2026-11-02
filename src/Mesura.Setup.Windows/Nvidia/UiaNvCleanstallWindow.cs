using System.Diagnostics;
using System.Windows.Automation;
using Mesura.Core.Nvidia;

namespace Mesura.Setup.Windows.Nvidia;

/// <summary>
/// NVCleanstall's window through UI Automation, the interface Windows gives assistive technology (the same one a screen reader uses): controls are found by the AutomationId
/// NVCleanstall gives them and operated through their patterns (select, value, invoke, toggle), never by position or by sending keys. A control that is not there, or not enabled,
/// is an error to the caller, not something to click around. NVCleanstall runs elevated (its installer says it elevates itself), so Mesura has to be elevated too for Windows to
/// let it drive the window.
/// </summary>
public sealed class UiaNvCleanstallWindow : INvCleanstallWindow
{
    private const string ProcessName = "NVCleanstall";

    private static AutomationElement? MainWindow()
    {
        var process = Process.GetProcessesByName(ProcessName).FirstOrDefault(p => !p.HasExited);
        if (process is null)
        {
            return null;
        }

        var children = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            if (child.Current.ProcessId == process.Id && child.Current.BoundingRectangle.Width > 200)
            {
                return child;
            }
        }

        return null;
    }

    private static AutomationElement? ById(string id) =>
        MainWindow()?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static AutomationElement Need(string id) =>
        ById(id) ?? throw new InvalidOperationException($"NVCleanstall's window has no control '{id}'.");

    public bool IsOpen => MainWindow() is not null;

    public string PageTitle
    {
        get
        {
            var scroll = MainWindow()?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ScrollViewer"));
            var text = scroll?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            return text?.Current.Name ?? "";
        }
    }

    public bool Has(string automationId) => ById(automationId) is not null;

    public bool IsEnabled(string automationId) => ById(automationId) is { } e && e.Current.IsEnabled;

    public string Text(string automationId)
    {
        var element = Need(automationId);
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : element.Current.Name;
    }

    public void Choose(string automationId) => ((SelectionItemPattern)Need(automationId).GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public void SetText(string automationId, string text) => ((ValuePattern)Need(automationId).GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    public void Press(string automationId) => ((InvokePattern)Need(automationId).GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public IReadOnlyList<NvListEntry> ListEntries(string automationId)
    {
        var entries = new List<NvListEntry>();
        foreach (AutomationElement item in Need(automationId).FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
        {
            var box = item.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
            if (box is null)
            {
                entries.Add(new NvListEntry(item.Current.Name, null, false)); // a heading
                continue;
            }

            var state = ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;
            entries.Add(new NvListEntry(item.Current.Name, state == ToggleState.On, box.Current.IsEnabled));
        }

        return entries;
    }

    public bool? SignatureRebuildTicked()
    {
        var boxes = MainWindow()?.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox));
        if (boxes is null)
        {
            return null;
        }

        foreach (AutomationElement box in boxes)
        {
            if (box.Current.Name.StartsWith("Rebuild digital signature", StringComparison.Ordinal))
            {
                return ((TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;
            }
        }

        return null;
    }

    public bool IsTicked(string automationId) => ((TogglePattern)Need(automationId).GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On;

    public void Tick(string automationId, bool on)
    {
        var toggle = (TogglePattern)Need(automationId).GetCurrentPattern(TogglePattern.Pattern);
        if ((toggle.Current.ToggleState == ToggleState.On) != on)
        {
            toggle.Toggle();
        }
    }

    public void SetChecked(string listAutomationId, string entryName, bool on)
    {
        var item = Need(listAutomationId).FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)).Cast<AutomationElement>()
            .FirstOrDefault(i => i.Current.Name == entryName) ?? throw new InvalidOperationException($"NVCleanstall's list has no row '{entryName}'.");
        var box = item.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
            ?? throw new InvalidOperationException($"'{entryName}' has no check box.");
        var toggle = (TogglePattern)box.GetCurrentPattern(TogglePattern.Pattern);
        if ((toggle.Current.ToggleState == ToggleState.On) != on)
        {
            toggle.Toggle();
        }
    }
}
