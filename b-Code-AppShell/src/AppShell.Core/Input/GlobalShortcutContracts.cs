namespace AppShell.Core.Input;

[Flags]
public enum GlobalShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
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

public sealed record GlobalShortcutRegistrationInfo(
    string Id,
    string Owner,
    IReadOnlyList<GlobalShortcutStroke> Strokes,
    string CommandText,
    int MaxIntervalMilliseconds);

/// <summary>Owner-scoped registration surface injected into shortcut modules.</summary>
public interface IGlobalShortcutRegistrar : IDisposable
{
    IDisposable Register(GlobalShortcutDescriptor descriptor);
}

/// <summary>
/// Optional module capability. Implementing this interface authorizes the module
/// to register command-backed global shortcuts for its lifetime.
/// </summary>
public interface IGlobalShortcutModule
{
    void RegisterShortcuts(IGlobalShortcutRegistrar registrar);
}
