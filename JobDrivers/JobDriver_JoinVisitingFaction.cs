using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace PeacefulFarewell
{
    public class JobDriver_JoinVisitingFaction : JobDriver
    {
        private const int RepathIntervalTicks = 60;
        private const int CloseEnoughDistSquared = 16;

        private Pawn RelatedPawn => job.targetA.Thing as Pawn;

        private PF_Job_JoinVisitingFaction FarewellJob => job as PF_Job_JoinVisitingFaction;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Faction is already cleared by the time this job starts - see
            // FarewellUtility.StartJoinJob for why (SetFaction force-ends
            // whatever job is running via ClearMind_NewTemp, so it can't
            // safely happen from inside this job's own initAction).
            yield return FarewellUtility.MakeRoomVisitToil(job.targetB.Thing as Building_Bed);

            Toil follow = new Toil();
            follow.defaultCompleteMode = ToilCompleteMode.Never;
            follow.initAction = delegate
            {
                TryRepath();
            };
            follow.tickIntervalAction = delegate(int delta)
            {
                if (FarewellJob == null)
                {
                    ReadyForNextToil();
                    return;
                }

                if (HasReachedExitPoint())
                {
                    ReadyForNextToil();
                    return;
                }

                if (!pawn.pather.Moving)
                {
                    TryRepath();
                }
            };
            follow.AddFailCondition(() => pawn.Downed || pawn.Dead);
            // Diagnostic mirroring JobDriver_LeaveOutOfWanderlust - see there
            // for why this is needed (a pawn can visibly leave the map without
            // the finish toil ever running or an exception appearing in
            // Player.log).
            follow.AddFinishAction(() =>
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"JobDriver_JoinVisitingFaction follow toil finish action for {pawn.LabelShort}: ended={ended}, spawned={pawn.Spawned}, map={pawn.Map}, position={pawn.Position}, faction={pawn.Faction?.Name ?? "null"}, discarded={pawn.Discarded}");
                }
            });
            yield return follow;

            Toil finish = new Toil();
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            finish.initAction = delegate
            {
                DoJoinFaction();
            };
            yield return finish;
        }

        private void TryRepath()
        {
            Pawn related = RelatedPawn;
            IntVec3 dest;

            if (related != null && related.Spawned && related.Map == pawn.Map)
            {
                dest = related.Position;
            }
            else if (RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot))
            {
                dest = exitSpot;
            }
            else
            {
                return;
            }

            pawn.pather.StartPath(dest, PathEndMode.Touch);
        }

        private bool HasReachedExitPoint()
        {
            Pawn related = RelatedPawn;
            Lord visitingLord = job.lord;
            bool visitorsLeaving = visitingLord == null
                || visitingLord.ownedPawns.Count == 0
                || visitingLord.CurLordToil is LordToil_ExitMap;

            if (related != null && related.Spawned && related.Map == pawn.Map)
            {
                if ((pawn.Position - related.Position).LengthHorizontalSquared <= CloseEnoughDistSquared)
                {
                    return true;
                }
                if (visitorsLeaving && pawn.Position.OnEdge(pawn.Map))
                {
                    return true;
                }
                return false;
            }

            return pawn.Position.OnEdge(pawn.Map) || pawn.Map.exitMapGrid.IsExitCell(pawn.Position);
        }

        private void DoJoinFaction()
        {
            if (FarewellJob == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            Faction targetFaction = FarewellJob.targetFaction;

            if (targetFaction == null || targetFaction.IsPlayer)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            FarewellUtility.ApplyJoinVisitingFactionThoughts(pawn);

            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }

            pawn.SetFaction(targetFaction);
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            FarewellUtility.SendJoinedLetter(pawn, targetFaction);
        }
    }
}
