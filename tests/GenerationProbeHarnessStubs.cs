// MMR-262: tools/Test-GenerationProbe.ps1 compiles Diagnostics/GenerationProbe.cs without the module's other sources. MMR-261's
// GenerationProbe.RunProducerApply names the fwg apply job types (World/WorldMutations.cs) and the loader's Busy flag
// (Persistence/GeneratedWorldLoader.cs), which need the engine assemblies; these stand-ins only let the probe compile. The harness never
// calls RunProducerApply: tools/Test-WorldMutation.ps1 compiles the real types and checks the apply launcher.
using System.Collections.Generic;

#pragma warning disable 649
namespace MNBL.ProceduralWorld.Surface
{
    internal sealed class ApplyJob { public string Producer, ProducerSha256, Directory; public List<string> Arguments; public int TimeoutSeconds; }
    internal sealed class ApplyOutcome
    {
        public bool Succeeded; public string Delta, Error;
        public static ApplyOutcome Finished(ApplyJob job, int exit, bool cancelled, bool timedOut, bool wroteDelta)
        {
            return new ApplyOutcome { Succeeded = !cancelled && !timedOut && exit == 0 && wroteDelta, Error = "harness stand-in" };
        }
    }
}

namespace MNBL.ProceduralWorld.Persistence
{
    internal static class GeneratedWorldLoader { public static bool Busy { get { return false; } } }
}
#pragma warning restore 649
