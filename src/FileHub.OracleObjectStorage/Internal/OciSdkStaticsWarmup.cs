using System.Runtime.CompilerServices;

namespace FileHub.OracleObjectStorage.Internal
{
    /// <summary>
    /// Forces the OCI SDK's <see cref="Oci.Common.Region"/> static initialization
    /// to run single-threaded, before any concurrent use of this assembly.
    /// The SDK's <c>Region</c> combines <c>[MethodImpl(Synchronized)]</c> static
    /// methods (which lock <c>typeof(Region)</c>) with static field initializers
    /// that call those same methods from the class constructor. Two threads
    /// touching <c>Region</c> for the first time concurrently — one through a
    /// static field, one through <c>FromRegionId</c> — acquire the type monitor
    /// and the CLR class-init lock in opposite orders and deadlock permanently.
    /// Warming the class constructor here makes that first touch race-free.
    /// </summary>
    internal static class OciSdkStaticsWarmup
    {
        // CA2255: a ModuleInitializer in a library is normally discouraged, but
        // this one exists precisely to make loading this assembly safe — it
        // serializes the OCI SDK's deadlock-prone static init before any
        // consumer touches Region. That is exactly the "library that must run
        // one-time setup on load" case the rule cannot see as legitimate.
#pragma warning disable CA2255
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void EnsureRegionInitialized()
            => _ = Oci.Common.Region.US_CHICAGO_1;
    }
}
