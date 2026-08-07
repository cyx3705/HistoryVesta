namespace AppShell.Core.Input;

/// <summary>Provides this AppShell public contract member.</summary>
[Flags]
public enum GlobalShortcutModifiers
{
    /// <summary>Provides this AppShell public contract member.</summary>
    None = 0,
    /// <summary>Provides this AppShell public contract member.</summary>
    Alt = 1,
    /// <summary>Provides this AppShell public contract member.</summary>
    Control = 2,
    /// <summary>Provides this AppShell public contract member.</summary>
    Shift = 4,
    /// <summary>Provides this AppShell public contract member.</summary>
    Windows = 8,
}

/// <summary>A physical virtual-key stroke used by a global shortcut.</summary>
public sealed record GlobalShortcutStroke(
    int VirtualKey,
    GlobalShortcutModifiers Modifiers = GlobalShortcutModifiers.None);

/// <summary>
/// A non-suppressing global shortcut. Strokes are evaluated in order; the host
/// rejects duplicate and prefix-conflicting registrations.
/// </summary>
public sealed record GlobalShortcutDescriptor(
    string Id,
    IReadOnlyList<GlobalShortcutStroke> Strokes,
    string CommandText,
    int MaxIntervalMilliseconds = 350);

/// <summary>Provides this AppShell public contract member.</summary>
public sealed record GlobalShortcutRegistrationInfo(
    string Id,
    string Owner,
    IReadOnlyList<GlobalShortcutStroke> Strokes,
    string CommandText,
    int MaxIntervalMilliseconds);

/// <summary>Owner-scoped registration surface injected into shortcut modules.</summary>
public interface IGlobalShortcutRegistrar : IDisposable
{
    /// <summary>Provides this AppShell public contract member.</summary>
    IDisposable Register(GlobalShortcutDescriptor descriptor);
}

/// <summary>
/// Optional module capability. Implementing this interface authorizes the module
/// to register command-backed global shortcuts for its lifetime.
/// </summary>
public interface IGlobalShortcutModule
{
    /// <summary>Provides this AppShell public contract member.</summary>
    void RegisterShortcuts(IGlobalShortcutRegistrar registrar);
}
