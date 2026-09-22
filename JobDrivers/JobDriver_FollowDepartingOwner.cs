using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace PeacefulFarewell
{
    // Runs on a bonded animal while its owner walks their own farewell job to
    // the map edge. Purely cosmetic follow - the owner's job is the one that
    // decides timing and never waits on this one. If the animal isn't close
    // enough when the owner actually disappears, it simply stays behind and
    // this job ends on its own.
    public class JobDriver_FollowDepartingOwner : JobDriver
    {
        // Matches JobDriver_JoinVisitingFaction's CloseEnoughDistSquared - the
        // animal counts as "with" the owner at the same range a departing
        // colonist counts as having reached a relation they're following.
        private const int CloseEnoughDistSquared = 16;

        private Pawn Owner => job.targetA.Thing as Pawn;

        private IntVec3 lastOwnerPosition;

        // The animal's normal allowed-area restriction can otherwise strand it
        // partway to the map edge (pather just stops moving once it hits the
        // zone boundary, with no fail condition tripped), leaving it too far
        // from the owner when they actually leave. Lifted for the duration of
        // this job and restored afterward regardless of outcome.
        private Area restoreAllowedArea;
        private bool restoreAllowedAreaSet;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil follow = new Toil();
            follow.defaultCompleteMode = ToilCompleteMode.Never;
            follow.initAction = delegate
            {
                if (pawn.playerSettings != null)
                {
                    restoreAllowedArea = pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
                    restoreAllowedAreaSet = true;
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = null;
                }

                Pawn owner = Owner;
                if (owner != null)
                {
                    lastOwnerPosition = owner.Position;
                }
                TryRepath();
            };
            follow.AddFinishAction(delegate
            {
                if (restoreAllowedAreaSet && pawn.playerSettings != null && pawn.Spawned)
                {
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = restoreAllowedArea;
                }
            });
            follow.tickIntervalAction = delegate(int delta)
            {
                Pawn owner = Owner;

                if (owner == null || owner.Dead)
                {
                    ReadyForNextToil();
                    return;
                }

                if (!owner.Spawned || owner.Map != pawn.Map)
                {
                    // The owner already left the map. If we were close enough right
                    // before that happened, go with them - otherwise stay behind.
                    if ((pawn.Position - lastOwnerPosition).LengthHorizontalSquared <= CloseEnoughDistSquared)
                    {
                        FarewellUtility.SendBondedAnimalAfterOwner(pawn, owner);
                    }
                    ReadyForNextToil();
                    return;
                }

                lastOwnerPosition = owner.Position;

                if (!pawn.pather.Moving)
                {
                    TryRepath();
                }
            };
            follow.AddFailCondition(() => pawn.Downed || pawn.Dead);
            yield return follow;
        }

        private void TryRepath()
        {
            Pawn owner = Owner;
            if (owner == null || !owner.Spawned || owner.Map != pawn.Map)
            {
                return;
            }

            lastOwnerPosition = owner.Position;
            pawn.pather.StartPath(owner.Position, PathEndMode.Touch);
        }
    }
}
