using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Anchor.Interop;

/// <summary>
/// The shell's COM objects, reached through .NET's source-generated COM (<c>[GeneratedComInterface]</c>) rather than
/// the runtime's built-in COM interop, which a natively compiled (AOT) build doesn't have. An object made here can be
/// cast to any generated interface it implements (a query for it is made then), and is let go of with
/// <see cref="Release"/> rather than <c>Marshal.ReleaseComObject</c>.
/// </summary>
internal static partial class ComInterop
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    /// <summary>Takes over a COM pointer and the reference it carries. Null for a null pointer.</summary>
    public static object? Take(nint pointer)
    {
        if (pointer == 0)
            return null;
        try
        {
            return Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer); // the wrapper holds its own reference
        }
    }

    /// <summary>Takes over a COM pointer as <typeparamref name="T"/>; null (and released) if it isn't one.</summary>
    public static T? Take<T>(nint pointer) where T : class
    {
        var taken = Take(pointer);
        if (taken is T wanted)
            return wanted;
        Release(taken);
        return null;
    }

    /// <summary>Makes the object registered as <paramref name="clsid"/>, as <typeparamref name="T"/>.</summary>
    public static T Create<T>(Guid clsid) where T : class
    {
        var iid = IID_IUnknown;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, ClsctxServer, in iid, out var pointer));
        return Take<T>(pointer) ?? throw new InvalidCastException($"{clsid:B} isn't a {typeof(T).Name}");
    }

    /// <summary>Lets go of an object made here now, rather than when the collector finds it.</summary>
    public static void Release(object? com)
    {
        if (com is ComObject wrapped)
            wrapped.FinalRelease();
    }

    // In process, or in another (as the runtime's own activation asks for).
    private const uint ClsctxServer = 0x1 | 0x4 | 0x10; // INPROC_SERVER | LOCAL_SERVER | REMOTE_SERVER

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}
