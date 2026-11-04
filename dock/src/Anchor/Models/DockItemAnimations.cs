namespace Anchor.Models;

/// <summary>App-wide visual flags that dock item bindings read without holding a window reference.</summary>
public static class DockItemAnimations
{
    public static bool ReducedMotion { get; set; }

    public static bool ShowLabels { get; set; }

    /// <summary>A High Contrast theme is on, so items drop any colour of their own (see
    /// <see cref="DockItem.IconColor"/>) and let Windows supply it.</summary>
    public static bool HighContrast { get; set; }

    public static event Action? ShowLabelsChanged;

    public static void SetShowLabels(bool on)
    {
        if (ShowLabels == on)
            return;
        ShowLabels = on;
        ShowLabelsChanged?.Invoke();
    }
}
