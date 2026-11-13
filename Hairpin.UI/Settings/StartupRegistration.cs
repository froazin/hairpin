using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Hairpin.UI.Settings;

/// <summary>
/// Starts the app at sign-in through the per-user Run key, which needs no admin rights. Respects Task Manager's
/// Startup apps switch: when the user has turned the entry off there, the app never turns it back on by itself.
/// Registry errors never throw: the methods report failure instead. An app installed from the MSIX package uses
/// <see cref="ForPackage"/> instead, because its package's startup task starts it.
/// </summary>
/// <param name="exePath">The exe to start at sign-in.</param>
/// <param name="enabled">False for development builds, so they never touch the installed build's entry.</param>
public sealed class StartupRegistration(string exePath, bool enabled)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Hairpin";

    private readonly string _exePath = exePath;

    /// <summary>The command the Run key holds.</summary>
    public string Command { get; } = "\"" + exePath + "\" --background";

    /// <summary>False when this build mustn't change the registry, such as a development build.</summary>
    public bool Enabled { get; } = enabled;

    /// <summary>
    /// True when the MSIX package's startup task starts the app, so the user turns it on or off in Windows Settings and
    /// the app never reads or writes the Run key.
    /// </summary>
    public bool ByPackage { get; private init; }

    /// <summary>
    /// For an app running from its MSIX package. A packaged app's writes to the Run key are virtualised, so Windows
    /// wouldn't see them, and its install path changes with every update, so it mustn't be recorded anywhere.
    /// </summary>
    public static StartupRegistration ForPackage() => new(string.Empty, enabled: false) { ByPackage = true };

    /// <summary>
    /// True when Windows will start this product at sign-in: the Run entry exists and the user hasn't turned it off in
    /// Task Manager.
    /// </summary>
    public static bool IsEffective() => ReadRunValue() is not null && !IsDisabledByUser();

    /// <summary>
    /// True when the user turned the entry off in Task Manager. The <c>StartupApproved</c> value isn't documented by Microsoft;
    /// an odd first byte meaning "disabled" is widely observed, so it's read defensively and a missing value means enabled.
    /// </summary>
    public static bool IsDisabledByUser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } value && (value[0] & 1) == 1;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Applies the setting when the app starts. When it's on, registers this exe unless the user turned startup off in Task
    /// Manager, or the entry already names another copy that still exists (so two installs don't fight over it). When it's
    /// off, removes the entry. Returns false when the registry couldn't be changed.
    /// </summary>
    public bool ApplyAtLaunch(bool startAtLogin)
    {
        if (!Enabled)
        {
            return true;
        }

        if (!startAtLogin)
        {
            return Unregister();
        }

        var existing = ReadRunValue();
        if (IsDisabledByUser() || (existing is not null && (existing == Command || TargetExists(existing))))
        {
            return true;
        }

        return Register();
    }

    /// <summary>
    /// Applies a change the user made in the settings window. Turning it on registers this exe and clears Task Manager's
    /// "disabled" mark, because the user has just asked for the app to start at sign-in. Returns false when the registry
    /// couldn't be changed.
    /// </summary>
    public bool ApplyUserChoice(bool startAtLogin)
    {
        if (!Enabled)
        {
            return true;
        }

        if (!startAtLogin)
        {
            return Unregister();
        }

        if (!Register())
        {
            return false;
        }

        return Try(() =>
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        });
    }

    private bool Register() => Try(() =>
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, Command, RegistryValueKind.String);
    });

    /// <summary>Removes the entry, whichever copy it names: the value name belongs to this product.</summary>
    private static bool Unregister() => Try(() =>
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    });

    private static string? ReadRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when a Run command's quoted exe path exists and isn't this exe.</summary>
    private bool TargetExists(string command)
    {
        var path = command.StartsWith('"') && command.IndexOf('"', 1) is var end and > 1 ? command[1..end] : command.Split(' ')[0];
        return !string.Equals(path, _exePath, StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    }

    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
