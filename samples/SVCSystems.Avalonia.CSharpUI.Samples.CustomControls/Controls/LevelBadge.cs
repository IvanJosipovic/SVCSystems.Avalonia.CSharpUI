using Avalonia;
using Avalonia.Controls;

namespace SVCSystems.Avalonia.CSharpUI.Samples.CustomControls.Controls;

/// <summary>
/// Example of a badge control with different levels of importance.
/// </summary>
public sealed class LevelBadge : ContentControl
{
    public static readonly StyledProperty<BadgeLevel> LevelProperty =
        AvaloniaProperty.Register<LevelBadge, BadgeLevel>(nameof(Level));

    public static readonly AttachedProperty<bool> EmphasizedProperty =
        AvaloniaProperty.RegisterAttached<LevelBadge, Control, bool>("Emphasized");

    /// <summary>
    /// Gets or sets the level of importance for the badge.
    /// </summary>
    [SVCSystemsCSharpUIGeneratorMetadata(Alias = "Importance")]
    public BadgeLevel Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public event EventHandler? Activated;

    public static bool GetEmphasized(AvaloniaObject element) => element.GetValue(EmphasizedProperty);

    public static void SetEmphasized(AvaloniaObject element, bool value) => element.SetValue(EmphasizedProperty, value);

    public void Activate() => Activated?.Invoke(this, EventArgs.Empty);
}

public enum BadgeLevel
{
    Normal,
    Important,
    Urgent
}
