#if !NET5_0_OR_GREATER
// Shim so C# 9 init-only setters compile on netstandard2.0.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
