#if !NET5_0_OR_GREATER
// Shim so [ModuleInitializer] compiles on netstandard2.0. The compiler emits
// the module initializer at the IL level, so it runs on any runtime.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif
