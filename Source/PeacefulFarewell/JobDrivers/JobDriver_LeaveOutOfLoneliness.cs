using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    public class JobDriver_LeaveOutOfLoneliness : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Faction is already cleared by the time this job starts - see
            // FarewellUtility.StartLeaveOutOfLonelinessJob for why (SetFaction
            // force-ends whatever job is running via ClearMind_NewTemp, so it
            // can't safely happen from inside this job's own initAction).
            yield return FarewellUtility.MakeRoomVisitToil(job.targetA.Thing as Building_Bed);

            Toil exit = new Toil();
            exit.defaultCompleteMode = ToilCompleteMode.Never;
            exit.initAction = delegate
            {
                TryStartExitPath();
            };
            exit.tickIntervalAction = delegate(int delta)
            {
                if (pawn.Position.OnEdge(pawn.Map) || pawn.Map.exitMapGrid.IsExitCell(pawn.Position))
                {
                    ReadyForNextToil();
                    return;
                }
                if (!pawn.pather.Moving)
                {
                    TryStartExitPath();
                }
            };
            exit.AddFailCondition(() => pawn.Downed || pawn.Dead);
            // Diagnostic mirroring JobDriver_LeaveOutOfWanderlust - see there
            // for why this is needed (a pawn can visibly leave the map without
            // the finish toil ever running or an exception appearing in
            // Player.log).
            exit.AddFinishAction(() =>
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"JobDriver_LeaveOutOfLoneliness exit toil finish action for {pawn.LabelShort}: ended={ended}, spawned={pawn.Spawned}, map={pawn.Map}, position={pawn.Position}, faction={pawn.Faction?.Name ?? "null"}, discarded={pawn.Discarded}");
                }
            });
            yield return exit;

            Toil finish = new Toil();
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            finish.initAction = delegate
            {
                FarewellUtility.BecomeWanderer(pawn);
            };
            yield return finish;
        }

        private void TryStartExitPath()
        {
            if (RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot))
            {
                pawn.pather.StartPath(exitSpot, PathEndMode.OnCell);
            }
        }
    }
}
