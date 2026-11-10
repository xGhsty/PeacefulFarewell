using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    // Shared by the loneliness and wanderlust departures: visit their own
    // room, walk to the map edge, then hand over to OnReachedEdge.
    //
    // The pawn's faction is already cleared by the time this job starts (see
    // FarewellUtility.StartLeaveOutOf*Job) - SetFaction force-ends whatever
    // job is running, so it can't happen from inside this job.
    public abstract class JobDriver_PF_LeaveMap : JobDriver
    {
        private const int MaxConsecutiveFailedExitAttempts = 5;

        private int failedExitAttempts;

        // Whether to give up (restore the player faction, send the "departure
        // failed" letter) after MaxConsecutiveFailedExitAttempts with no
        // reachable exit spot, instead of retrying forever.
        protected abstract bool GiveUpWithoutExit { get; }

        protected abstract void OnReachedEdge();

        private string Name => GetType().Name;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return FarewellUtility.MakeRoomVisitToil(job.targetA.Thing as Building_Bed);

            Toil exit = new Toil();
            exit.defaultCompleteMode = ToilCompleteMode.Never;
            exit.initAction = delegate
            {
                if (PF_Log.DebugMode)
                    PF_Log.Message($"{Name} exit toil for {pawn.LabelShort}: starting at {pawn.Position}, map={pawn.Map}");
                TryStartExitPath();
            };
            exit.tickIntervalAction = delegate(int delta)
            {
                if (pawn.Position.OnEdge(pawn.Map) || pawn.Map.exitMapGrid.IsExitCell(pawn.Position))
                {
                    if (PF_Log.DebugMode)
                        PF_Log.Message($"{Name} exit toil for {pawn.LabelShort}: reached edge at {pawn.Position}, advancing to finish toil.");
                    ReadyForNextToil();
                    return;
                }
                if (!pawn.pather.Moving)
                {
                    TryStartExitPath();
                }
            };
            exit.AddFailCondition(() =>
            {
                bool fail = pawn.Downed || pawn.Dead;
                if (fail && PF_Log.DebugMode)
                    PF_Log.Message($"{Name} exit toil for {pawn.LabelShort}: fail condition hit (downed={pawn.Downed}, dead={pawn.Dead}) - job will end WITHOUT registering with PF_WandererTracker.");
                return fail;
            });
            // Runs on every job end path (completion, interruption, error), to
            // show in the debug log how a departure ended if the pawn left the
            // map without the finish toil ever running.
            exit.AddFinishAction(() =>
            {
                if (PF_Log.DebugMode)
                    PF_Log.Message($"{Name} exit toil finish action for {pawn.LabelShort}: ended={ended}, spawned={pawn.Spawned}, map={pawn.Map}, position={pawn.Position}, faction={pawn.Faction?.Name ?? "null"}, discarded={pawn.Discarded}");
            });
            yield return exit;

            Toil finish = new Toil();
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            finish.initAction = delegate
            {
                if (PF_Log.DebugMode)
                    PF_Log.Message($"{Name} finish toil for {pawn.LabelShort}: leaving the map.");
                OnReachedEdge();
            };
            yield return finish;
        }

        private void TryStartExitPath()
        {
            if (RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot))
            {
                failedExitAttempts = 0;
                pawn.pather.StartPath(exitSpot, PathEndMode.OnCell);
                return;
            }

            if (!GiveUpWithoutExit || ++failedExitAttempts < MaxConsecutiveFailedExitAttempts)
            {
                return;
            }

            if (PF_Log.DebugMode)
                PF_Log.Message($"{Name} for {pawn.LabelShort}: no reachable exit spot after {MaxConsecutiveFailedExitAttempts} attempts - bailing out, restoring player faction, job will end WITHOUT registering with PF_WandererTracker.");
            pawn.SetFaction(Faction.OfPlayer);
            FarewellUtility.SendDepartureFailedLetter(pawn);
            EndJobWith(JobCondition.Incompletable);
        }
    }

    public class JobDriver_LeaveOutOfLoneliness : JobDriver_PF_LeaveMap
    {
        protected override bool GiveUpWithoutExit => false;

        protected override void OnReachedEdge()
        {
            FarewellUtility.BecomeWanderer(pawn);
        }
    }

    public class JobDriver_LeaveOutOfWanderlust : JobDriver_PF_LeaveMap
    {
        protected override bool GiveUpWithoutExit => true;

        protected override void OnReachedEdge()
        {
            FarewellUtility.BecomeWanderlustWanderer(pawn);
        }
    }
}
