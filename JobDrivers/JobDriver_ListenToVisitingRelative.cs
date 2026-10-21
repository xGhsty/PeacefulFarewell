using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    // The visitor's half of JobDriver_TalkWithVisitingRelative: stand still
    // and face the colonist for as long as they're still talking to this
    // pawn, so the conversation doesn't turn into a chase across the map.
    // Purely cosmetic - the talk works without it (the colonist follows),
    // and the job carries an expiry as a safety net.
    public class JobDriver_ListenToVisitingRelative : JobDriver
    {
        private Pawn Colonist => job.targetA.Thing as Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil listen = new Toil();
            listen.defaultCompleteMode = ToilCompleteMode.Never;
            listen.socialMode = RandomSocialMode.Off;
            listen.handlingFacing = true;
            listen.initAction = delegate
            {
                pawn.pather.StopDead();
            };
            listen.tickAction = delegate
            {
                if (Colonist != null)
                {
                    pawn.rotationTracker.FaceTarget(Colonist);
                }
            };
            listen.tickIntervalAction = delegate(int delta)
            {
                if (!IsBeingTalkedTo())
                {
                    EndJobWith(JobCondition.Succeeded);
                }
            };
            yield return listen;
        }

        private bool IsBeingTalkedTo()
        {
            Pawn colonist = Colonist;
            return colonist != null
                && colonist.Spawned
                && colonist.Map == pawn.Map
                && colonist.CurJobDef == PF_JobDefOf.PF_TalkWithVisitingRelative
                && colonist.CurJob.targetA.Thing == pawn;
        }
    }
}
