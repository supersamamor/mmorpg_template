#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices
{
    // Lets records and `init` accessors compile on netstandard2.1.
    internal static class IsExternalInit
    {
    }
}
#endif
