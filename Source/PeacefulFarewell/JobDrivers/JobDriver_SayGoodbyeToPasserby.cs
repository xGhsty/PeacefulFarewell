using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    // The player refused a passerby's join request (ChoiceLetter_JoinRequest
    // with passerby = true): the colonist runs after the traveler before
    // they're gone, the traveler stops (JobDriver_ListenToVisitingRelative),
    // and they say goodbye. Made it -> PF_FarewellToPasserby. Didn't (the
    // traveler left the map, the colonist got drafted, ...) ->
    // PF_MissedGoodbye.
    public class JobDriver_SayGoodbyeToPasserby : JobDriver
    {
        private const int RepathIntervalTicks = 60;
        private const float TalkRangeSquared = 9f;
        private const float InteractRange = 6f;
        private const int MaxApproachTicks = 2500;
        private const int LineRetryTicks = 60;
        private const int GoodbyeDurationTicks = 400;
        private const int MaxLineAttemptsTicks = 600;

        private int approachStartTick = -1;
        private int goodbyeStartTick = -1;
        private int goodbyeEndTick = -1;
        private bool lineSaid;
        private bool thoughtGiven;

        private Pawn Relative => job.targetA.Thing as Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => Relative == null || Relative.Dead || !Relative.Spawned || Relative.Map != pawn.Map);
            AddFinishAction(delegate(JobCondition condition)
            {
                if (thoughtGiven || pawn.Dead)
                {
                    return;
                }
                thoughtGiven = true;
                // Once the goodbye was said, cutting the hug short afterwards
                // doesn't make it a missed goodbye.
                if (lineSaid)
                {
                    FarewellUtility.GiveFarewellToPasserbyThought(pawn, Relative);
                }
                else
                {
                    FarewellUtility.GiveMissedGoodbyeThought(pawn, Relative);
                }
            });

            Toil approach = new Toil();
            approach.defaultCompleteMode = ToilCompleteMode.Never;
            approach.initAction = delegate
            {
                if (approachStartTick < 0)
                {
                    approachStartTick = Find.TickManager.TicksGame;
                }
                pawn.pather.StartPath(Relative, PathEndMode.Touch);
                FarewellUtility.TryMakeVisitorListen(Relative, pawn, MaxApproachTicks + MaxLineAttemptsTicks + GoodbyeDurationTicks);
            };
            approach.tickIntervalAction = delegate(int delta)
            {
                if (IsInTalkRange())
                {
                    ReadyForNextToil();
                    return;
                }
                if (Find.TickManager.TicksGame - approachStartTick > MaxApproachTicks)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (!pawn.pather.Moving || GenTicks.IsTickIntervalDelta(pawn.thingIDNumber, RepathIntervalTicks, delta))
                {
                    pawn.pather.StartPath(Relative, PathEndMode.Touch);
                }
            };
            yield return approach;

            Toil goodbye = new Toil();
            goodbye.defaultCompleteMode = ToilCompleteMode.Never;
            goodbye.socialMode = RandomSocialMode.Off;
            goodbye.handlingFacing = true;
            goodbye.initAction = delegate
            {
                pawn.pather.StopDead();
                if (goodbyeStartTick < 0)
                {
                    goodbyeStartTick = Find.TickManager.TicksGame;
                }
                FarewellUtility.TryMakeVisitorListen(Relative, pawn, MaxLineAttemptsTicks + GoodbyeDurationTicks);
            };
            goodbye.tickAction = delegate
            {
                if (Relative != null)
                {
                    pawn.rotationTracker.FaceTarget(Relative);
                }
            };
            goodbye.tickIntervalAction = delegate(int delta)
            {
                if (!IsInTalkRange())
                {
                    if (!pawn.pather.Moving)
                    {
                        pawn.pather.StartPath(Relative, PathEndMode.Touch);
                    }
                }
                else if (pawn.pather.Moving)
                {
                    pawn.pather.StopDead();
                }

                int now = Find.TickManager.TicksGame;
                if (!lineSaid)
                {
                    if (GenTicks.IsTickIntervalDelta(pawn.thingIDNumber, LineRetryTicks, delta) && TrySayGoodbye())
                    {
                        lineSaid = true;
                        goodbyeEndTick = now + GoodbyeDurationTicks;
                    }
                    else if (now - goodbyeStartTick > MaxLineAttemptsTicks)
                    {
                        EndJobWith(JobCondition.Incompletable);
                    }
                    return;
                }

                if (now >= goodbyeEndTick)
                {
                    EndJobWith(JobCondition.Succeeded);
                }
            };
            yield return goodbye;
        }

        private bool IsInTalkRange()
        {
            Pawn relative = Relative;
            return relative != null
                && (pawn.Position - relative.Position).LengthHorizontalSquared <= TalkRangeSquared
                && GenSight.LineOfSight(pawn.Position, relative.Position, pawn.Map, skipFirstCell: true);
        }

        private bool TrySayGoodbye()
        {
            Pawn relative = Relative;
            if (relative == null || pawn.interactions == null || !pawn.Position.InHorDistOf(relative.Position, InteractRange))
            {
                return false;
            }
            return pawn.interactions.TryInteractWith(relative, PF_InteractionDefOf.PF_PasserbyGoodbye);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref approachStartTick, "approachStartTick", -1);
            Scribe_Values.Look(ref goodbyeStartTick, "goodbyeStartTick", -1);
            Scribe_Values.Look(ref goodbyeEndTick, "goodbyeEndTick", -1);
            Scribe_Values.Look(ref lineSaid, "lineSaid");
            Scribe_Values.Look(ref thoughtGiven, "thoughtGiven");
        }
    }
}
