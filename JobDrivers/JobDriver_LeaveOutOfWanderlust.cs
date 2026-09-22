using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    public class JobDriver_LeaveOutOfWanderlust : JobDriver
    {
        private const int MaxConsecutiveFailedExitAttempts = 5;

        private int failedExitAttempts;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Faction is already cleared by the time this job starts - see
            // FarewellUtility.StartLeaveOutOfWanderlustJob for why (SetFaction
            // force-ends whatever job is running via ClearMind_NewTemp, so it
            // can't safely happen from inside this job's own initAction).
            yield return FarewellUtility.MakeRoomVisitToil(job.targetA.Thing as Building_Bed);

            Toil exit = new Toil();
            exit.defaultCompleteMode = ToilCompleteMode.Never;
            exit.initAction = delegate
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"JobDriver_LeaveOutOfWanderlust exit toil for {pawn.LabelShort}: starting at {pawn.Position}, map={pawn.Map}");
                }
                TryStartExitPath();
            };
            exit.tickIntervalAction = delegate(int delta)
            {
                if (pawn.Position.OnEdge(pawn.Map) || pawn.Map.exitMapGrid.IsExitCell(pawn.Position))
                {
                    if (PeacefulFarewellMod.Settings.debugMode)
                    {
                        PF_Log.Message($"JobDriver_LeaveOutOfWanderlust exit toil for {pawn.LabelShort}: reached edge at {pawn.Position}, advancing to finish toil.");
                    }
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
                if (fail && PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"JobDriver_LeaveOutOfWanderlust exit toil for {pawn.LabelShort}: fail condition hit (downed={pawn.Downed}, dead={pawn.Dead}) - job will end WITHOUT registering with PF_WandererTracker.");
                }
                return fail;
            });
            // Diagnostic for a report where a pawn visibly left the map but
            // PF_WandererTracker never showed them as tracked afterward - the
            // finish toil (which calls BecomeWanderlustWanderer/Register) never
            // logged, and no exception showed up in Player.log either, so the
            // job must have ended some other way (interrupted by another
            // mod/vanilla system while faction-less). AddFinishAction runs on
            // every job end path - normal completion, interruption, error -
            // so this will show whether the job even reached this toil's end,
            // and the pawn's state at that moment, next time it happens.
            exit.AddFinishAction(() =>
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"JobDriver_LeaveOutOfWanderlust exit toil finish action for {pawn.LabelShort}: ended={ended}, spawned={pawn.Spawned}, map={pawn.Map}, position={pawn.Position}, faction={pawn.Faction?.Name ?? "null"}, discarded={pawn.Discarded}");
                }
            });
            yield return exit;

            Toil finish = new Toil();
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            finish.initAction = delegate
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"JobDriver_LeaveOutOfWanderlust finish toil for {pawn.LabelShort}: calling BecomeWanderlustWanderer.");
                }
                FarewellUtility.BecomeWanderlustWanderer(pawn);
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

            failedExitAttempts++;
            if (failedExitAttempts < MaxConsecutiveFailedExitAttempts)
            {
                return;
            }

            // No reachable exit spot after repeated attempts - bail out instead of
            // silently stalling the pawn in place forever.
            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"JobDriver_LeaveOutOfWanderlust for {pawn.LabelShort}: no reachable exit spot after {MaxConsecutiveFailedExitAttempts} attempts - bailing out, restoring player faction, job will end WITHOUT registering with PF_WandererTracker.");
            }
            pawn.SetFaction(Faction.OfPlayer);
            FarewellUtility.SendDepartureFailedLetter(pawn);
            EndJobWith(JobCondition.Incompletable);
        }
    }
}
