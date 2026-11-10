using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    // Carries the target faction alongside the job so it survives save/load
    // via Pawn_JobTracker's normal polymorphic job saving. The visiting Lord
    // uses vanilla Job's own scribed "lord" field.
    //
    // IExposable is re-declared on purpose: Job.ExposeData() isn't virtual,
    // so without re-implementing the interface here Scribe would keep calling
    // Job's version and targetFaction would never be saved.
    public class PF_Job_JoinVisitingFaction : Job, IExposable
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

        public new void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref targetFaction, "targetFaction");
        }
    }
}
