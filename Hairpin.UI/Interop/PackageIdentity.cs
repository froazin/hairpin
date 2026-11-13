namespace Hairpin.UI.Interop;

/// <summary>
/// Whether the process runs from an MSIX package, which changes where the app keeps its files and how it starts at
/// sign-in. Uses the documented <c>GetCurrentPackageFamilyName</c> rather than the WinRT projection, so the zip build
/// doesn't need the Windows SDK.
/// </summary>
public static class PackageIdentity
{
    /// <summary>
    /// The package family name, such as <c>Hairpin_1a2b3c4d5e6f7</c>, or null when the process isn't packaged.
    /// Any error counts as not packaged, and it never throws.
    /// </summary>
    public static unsafe string? FamilyName()
    {
        try
        {
            var length = (uint)NativeMethods.PackageFamilyNameBufferLength;
            var buffer = stackalloc char[NativeMethods.PackageFamilyNameBufferLength];
            if (NativeMethods.GetCurrentPackageFamilyName(ref length, buffer) != NativeMethods.ERROR_SUCCESS
                || length is 0 or > NativeMethods.PackageFamilyNameBufferLength)
            {
                // APPMODEL_ERROR_NO_PACKAGE for an unpackaged process, or anything else unexpected.
                return null;
            }

            // The length includes the terminating null.
            var name = new string(buffer, 0, (int)length - 1);
            return name.Length == 0 ? null : name;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }
}
