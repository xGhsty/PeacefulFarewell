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
            // Pawn already belongs to the target faction by the time this job
            // starts - see
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
                if (PF_Log.DebugMode)
                    PF_Log.Message($"JobDriver_JoinVisitingFaction follow toil finish action for {pawn.LabelShort}: ended={ended}, spawned={pawn.Spawned}, map={pawn.Map}, position={pawn.Position}, faction={pawn.Faction?.Name ?? "null"}, discarded={pawn.Discarded}");
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
            // Captured up front - the fallback path below despawns the pawn
            // before registering them, which clears pawn.Map.
            Map originMap = pawn.Map;

            if (targetFaction == null || targetFaction.IsPlayer)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            // Reached the visitors while they're still here: actually join
            // their group so the pawn stays with them and walks off the map
            // together when the caravan leaves. Previously the pawn was
            // despawned on the spot right next to the visitors, which looked
            // like they vanished/left alone while the caravan stayed behind
            // (user report, 2026-10-04).
            Lord visitingLord = job.lord;

            // Hospitality guests can stay for days, and adding the pawn to
            // their guest lord would turn them into a Hospitality guest
            // (guest rooms, recruitable again, ...). Instead: having met
            // their relative, the pawn goes ahead and walks off the map on
            // their own, already a member of the target faction.
            if (pawn.Spawned && FarewellUtility.IsHospitalityGuestLord(visitingLord)
                && RCellFinder.TryFindBestExitSpot(pawn, out IntVec3 exitSpot))
            {
                if (pawn.Faction != targetFaction)
                {
                    pawn.SetFaction(targetFaction);
                }

                FarewellUtility.ApplyJoinVisitingFactionThoughts(pawn);
                Job exitJob = JobMaker.MakeJob(JobDefOf.Goto, exitSpot);
                exitJob.exitMapOnArrival = true;
                pawn.jobs.jobQueue.EnqueueFirst(exitJob);
                if (PF_Log.DebugMode)
                    PF_Log.Message($"DoJoinFaction for {pawn.LabelShort}: met relative of Hospitality guest group, walking off the map to {exitSpot} as {targetFaction.Name}.");
                FarewellUtility.SendJoinedLetter(pawn, targetFaction);
                FarewellUtility.RegisterJoinedVisitors(pawn, originMap);
                return;
            }

            if (pawn.Spawned && CanJoinLord(visitingLord, targetFaction))
            {
                if (pawn.Faction != targetFaction)
                {
                    pawn.SetFaction(targetFaction);
                }

                FarewellUtility.ApplyJoinVisitingFactionThoughts(pawn);
                visitingLord.AddPawn(pawn);
                if (PF_Log.DebugMode)
                    PF_Log.Message($"DoJoinFaction for {pawn.LabelShort}: joined visiting lord ({visitingLord.LordJob?.GetType().Name}, {visitingLord.ownedPawns.Count} pawns) of {targetFaction.Name}.");
                FarewellUtility.SendJoinedLetter(pawn, targetFaction);
                FarewellUtility.RegisterJoinedVisitors(pawn, originMap);
                return;
            }

            // Fallback: the visitors already left (or the lord is gone), so
            // there's no group to walk out with - the pawn has walked to the
            // map edge on their own and leaves from there.
            FarewellUtility.ApplyJoinVisitingFactionThoughts(pawn);

            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }

            // Normally already set in FarewellUtility.StartJoinJob - kept as a
            // safety net for jobs saved mid-walk by an older mod version,
            // when the pawn was still factionless at this point.
            if (pawn.Faction != targetFaction)
            {
                pawn.SetFaction(targetFaction);
            }
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            FarewellUtility.SendJoinedLetter(pawn, targetFaction);
            FarewellUtility.RegisterJoinedVisitors(pawn, originMap);
        }

        private bool CanJoinLord(Lord lord, Faction targetFaction)
        {
            return lord != null
                && lord.faction == targetFaction
                && lord.ownedPawns.Count > 0
                && pawn.GetLord() == null
                && lord.Map == pawn.Map
                && !targetFaction.HostileTo(Faction.OfPlayer);
        }
    }
}
