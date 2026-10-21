using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    // Carries the target faction alongside the job so it survives save/load via
    // Pawn_JobTracker's normal Scribe_Deep polymorphic job saving - unlike the old
    // PF_PendingFarewell static dictionary, which was lost on reload and left
    // mid-departure pawns factionless. The visiting Lord doesn't need a field of
    // its own here since vanilla Job already has a scribed "lord" field.
    public class PF_Job_JoinVisitingFaction : Job
    {
        public Faction targetFaction;

        public PF_Job_JoinVisitingFaction()
        {
        }

        public PF_Job_JoinVisitingFaction(JobDef def, LocalTargetInfo targetA, Faction targetFaction)
            : base(def, targetA)
        {
            this.targetFaction = targetFaction;
        }

        // Job.ExposeData() is not virtual - Scribe_Deep dispatches through the
        // IExposable interface reference, so a `new` redeclaration here is still
        // called correctly on the concrete subclass instance.
        public new void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref targetFaction, "targetFaction");
        }
    }
}
