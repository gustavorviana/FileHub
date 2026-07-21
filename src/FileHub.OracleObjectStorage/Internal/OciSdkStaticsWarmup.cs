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
        [ModuleInitializer]
        internal static void EnsureRegionInitialized()
            => _ = Oci.Common.Region.US_CHICAGO_1;
    }
}
