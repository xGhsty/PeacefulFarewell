using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace PeacefulFarewell
{
    public static class FarewellUtility
    {
        private const int MinRoomVisitTicks = 2500; // 1 in-game hour
        private const int MaxRoomVisitTicks = 7500; // 3 in-game hours

        // While lingering in the room, the pawn periodically wanders to a new
        // random spot within it instead of just standing still - purely
        // cosmetic, re-rolled roughly every 4-10 seconds of game time.
        private const int MinWanderIntervalTicks = 250;
        private const int MaxWanderIntervalTicks = 600;

        // Shared by all 3 departure JobDrivers: before heading for the map edge,
        // a colonist with an assigned bed stops by their room first and lingers
        // for 1-3 in-game hours, purely for immersion (a moment to take in the
        // place they're leaving) before the actual exit toil begins. The linger
        // timer only starts once they've actually arrived at the bed, not from
        // toil start - the walk there doesn't count against it. While lingering,
        // the pawn periodically wanders to another spot in the same room instead
        // of standing still at the bed the whole time. Colonists with no owned
        // bed (e.g. never assigned one) skip straight to the exit toil.
        //
        // Takes the bed as a parameter (captured by the caller before clearing
        // the pawn's faction) rather than reading pawn.ownership.OwnedBed live -
        // SetFaction(null) wipes ownership as a side effect, and by the time
        // this toil runs the pawn is already faction-less (see StartXJob's
        // callers, which now clear faction up front - see the SetFaction(null)
        // job-suicide comment on the exit toils for why it can no longer happen
        // mid-job).
        public static Toil MakeRoomVisitToil(Building_Bed bed)
        {
            Toil visit = new Toil();
            visit.defaultCompleteMode = ToilCompleteMode.Never;
            int arrivedTick = -1;
            int nextWanderTick = -1;

            visit.initAction = delegate
            {
                Pawn pawn = visit.actor;
                if (bed == null || !bed.Spawned || bed.Map != pawn.Map)
                {
                    if (PeacefulFarewellMod.Settings.debugMode)
                    {
                        PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: skipping room visit - bed={(bed == null ? "null" : bed.LabelShort)}, spawned={bed?.Spawned}, bedMap={bed?.Map}, pawnMap={pawn.Map}");
                    }
                    visit.actor.jobs.curDriver.ReadyForNextToil();
                    return;
                }

                IntVec3 sleepSpot = RestUtility.GetBedSleepingSlotPosFor(pawn, bed);
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: heading to bed {bed.LabelShort} at {sleepSpot}, pawn currently at {pawn.Position}.");
                }
                pawn.pather.StartPath(sleepSpot, PathEndMode.OnCell);
            };
            visit.tickIntervalAction = delegate(int delta)
            {
                Pawn pawn = visit.actor;
                if (bed == null || !bed.Spawned || bed.Map != pawn.Map)
                {
                    visit.actor.jobs.curDriver.ReadyForNextToil();
                    return;
                }

                if (arrivedTick < 0)
                {
                    // Still walking to the bed the first time - not "arrived" yet.
                    if (pawn.pather.Moving)
                    {
                        return;
                    }

                    arrivedTick = Find.TickManager.TicksGame + Rand.RangeInclusive(MinRoomVisitTicks, MaxRoomVisitTicks);
                    nextWanderTick = Find.TickManager.TicksGame + Rand.RangeInclusive(MinWanderIntervalTicks, MaxWanderIntervalTicks);
                    if (PeacefulFarewellMod.Settings.debugMode)
                    {
                        PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: arrived at bed, lingering until tick {arrivedTick} (current {Find.TickManager.TicksGame}).");
                    }
                    return;
                }

                if (Find.TickManager.TicksGame >= arrivedTick)
                {
                    if (PeacefulFarewellMod.Settings.debugMode)
                    {
                        PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: linger done, moving to next toil.");
                    }
                    visit.actor.jobs.curDriver.ReadyForNextToil();
                    return;
                }

                // Still lingering - wander to a new spot in the room every so
                // often instead of standing still, as long as not already
                // walking somewhere.
                if (!pawn.pather.Moving && Find.TickManager.TicksGame >= nextWanderTick)
                {
                    nextWanderTick = Find.TickManager.TicksGame + Rand.RangeInclusive(MinWanderIntervalTicks, MaxWanderIntervalTicks);
                    IntVec3 wanderSpot = FindRoomWanderSpot(pawn, bed);
                    if (wanderSpot.IsValid && wanderSpot != pawn.Position)
                    {
                        pawn.pather.StartPath(wanderSpot, PathEndMode.OnCell);
                    }
                }
            };
            visit.AddFailCondition(() => visit.actor.Downed || visit.actor.Dead);
            return visit;
        }

        // Picks a random standable cell in the same room as the pawn's bed for
        // the room-visit wander. Falls back to the bed's own sleeping spot (or
        // an invalid cell, if even that fails) rather than throwing if the
        // room can't be resolved.
        private static IntVec3 FindRoomWanderSpot(Pawn pawn, Building_Bed bed)
        {
            IntVec3 sleepSpot = RestUtility.GetBedSleepingSlotPosFor(pawn, bed);
            Room room = bed.GetRoom();
            if (room == null || room.PsychologicallyOutdoors)
            {
                return sleepSpot;
            }

            List<IntVec3> cells = room.Cells.Where((IntVec3 c) => c.Standable(pawn.Map)).ToList();
            if (cells.Count == 0)
            {
                return sleepSpot;
            }

            // A handful of random tries rather than filtering the whole room by
            // CanReach every call - room-visit wandering is cosmetic and cheap
            // is more important here than guaranteeing the very best pick.
            for (int i = 0; i < 5; i++)
            {
                IntVec3 candidate = cells.RandomElement();
                if (pawn.CanReach(candidate, PathEndMode.OnCell, Danger.None))
                {
                    return candidate;
                }
            }

            return sleepSpot;
        }

        public static bool IsRunningFarewellJob(Pawn pawn)
        {
            JobDef curJobDef = pawn.CurJobDef;
            return curJobDef == PF_JobDefOf.PF_JoinVisitingFaction
                || curJobDef == PF_JobDefOf.PF_LeaveOutOfLoneliness
                || curJobDef == PF_JobDefOf.PF_LeaveOutOfWanderlust
                || curJobDef == PF_JobDefOf.PF_FollowDepartingOwner;
        }

        public struct RequestCandidate
        {
            public Pawn colonist;
            public Pawn relatedPawn;
            public Lord visitingLord;
            public Faction targetFaction;
        }

        public static bool TryFindRequestCandidate(Map map, out RequestCandidate candidate)
        {
            candidate = default(RequestCandidate);

            if (map == null || !map.IsPlayerHome)
            {
                return false;
            }

            List<RequestCandidate> options = new List<RequestCandidate>();

            int lordsTotal = map.lordManager.lords.Count;
            int lordsEligible = 0;
            int colonistsChecked = 0;

            foreach (Lord lord in map.lordManager.lords.ToList())
            {
                if (!IsEligibleVisitingLord(lord))
                {
                    continue;
                }
                lordsEligible++;

                foreach (Pawn colonist in map.mapPawns.FreeColonistsSpawned.ToList())
                {
                    if (!IsEligibleAdultColonist(colonist))
                    {
                        continue;
                    }
                    if (HasFamilyInColony(colonist))
                    {
                        continue;
                    }
                    if (PF_GameComponent.IsJoinRequestOnCooldown(colonist))
                    {
                        continue;
                    }
                    // A colonist with several real (memory-based) close
                    // friendships already in the colony has more to lose than
                    // gain from leaving for one visiting friend/relative -
                    // per user request, don't offer this choice to a colonist
                    // who'd be abandoning a bigger support network for a
                    // smaller one. HasFamilyInColony above already excludes
                    // colonists with formal family here; this catches the
                    // plain-friendship case that check doesn't cover.
                    if (CountStrongFriendsInColony(colonist) >= 2)
                    {
                        continue;
                    }
                    colonistsChecked++;

                    Pawn closePawn = FindClosestRelation(colonist, lord.ownedPawns.ToList());
                    if (closePawn != null)
                    {
                        options.Add(new RequestCandidate
                        {
                            colonist = colonist,
                            relatedPawn = closePawn,
                            visitingLord = lord,
                            targetFaction = lord.faction
                        });
                    }
                }
            }

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"TryFindRequestCandidate on {map}: lordsTotal={lordsTotal}, lordsEligible={lordsEligible}, colonistsChecked={colonistsChecked}, options={options.Count}, minImportance={PeacefulFarewellMod.Settings.minRelationImportance}");
            }

            if (options.Count == 0)
            {
                return false;
            }

            candidate = options.RandomElement();
            return true;
        }

        private static bool IsEligibleVisitingLord(Lord lord)
        {
            if (lord.faction == null || lord.faction == Faction.OfPlayer)
            {
                return false;
            }
            if (lord.faction.HostileTo(Faction.OfPlayer) || lord.faction.Hidden)
            {
                return false;
            }
            if (!(lord.LordJob is LordJob_VisitColony) && !(lord.LordJob is LordJob_TradeWithColony))
            {
                return false;
            }
            if (lord.CurLordToil is LordToil_ExitMap)
            {
                return false;
            }
            return lord.ownedPawns.Any((Pawn p) => p.RaceProps.Humanlike);
        }

        public static bool IsEligibleColonist(Pawn pawn)
        {
            if (!pawn.IsFreeColonist)
            {
                return false;
            }
            if (pawn.Downed || pawn.Drafted || pawn.IsPrisoner || pawn.IsSlave)
            {
                return false;
            }
            if (pawn.Dead || pawn.mindState == null)
            {
                return false;
            }
            if (IsRunningFarewellJob(pawn))
            {
                return false;
            }
            return true;
        }

        // Leaving to join a visiting relation or out of loneliness is a decision
        // only an adult should be able to make on their own - children can't be
        // drafted or work unsupervised either. Wanderlust deliberately stays on
        // its own 13-18 age band via IsWanderlustEligible instead of this check.
        private static bool IsEligibleAdultColonist(Pawn pawn)
        {
            return IsEligibleColonist(pawn) && pawn.DevelopmentalStage.Adult();
        }

        // Internal (not private) so PF_WandererTracker.FindFamilyFactionCandidates
        // can reuse the exact same "counts as family" definition when weighting
        // which faction a departing wanderer is more likely to settle with.
        internal static readonly HashSet<PawnRelationDef> StayForFamilyRelations = new HashSet<PawnRelationDef>
        {
            PawnRelationDefOf.Spouse,
            PawnRelationDefOf.Lover,
            PawnRelationDefOf.Fiance,
            PawnRelationDefOf.Child,
            PawnRelationDefOf.Parent,
            PawnRelationDefOf.Sibling,
        };

        private static bool HasFamilyInColony(Pawn pawn)
        {
            if (pawn.relations == null)
            {
                return false;
            }

            foreach (Pawn other in pawn.Map.mapPawns.FreeColonistsSpawned)
            {
                if (other == pawn || other.Dead)
                {
                    continue;
                }

                // PawnRelationUtility.GetRelations (not just DirectRelations) so
                // virtual/inferred kinships like Sibling (computed from shared
                // parents, not always a physical DirectRelation entry) still
                // count - see the matching comment on GetLovedOneCloseness below,
                // which hit the same gap for the wanderlust-return notify.
                foreach (PawnRelationDef relationDef in PawnRelationUtility.GetRelations(pawn, other))
                {
                    if (StayForFamilyRelations.Contains(relationDef))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        // Same "good friend" bar used elsewhere (relations tab parity, and
        // PF_WandererTracker's family-faction weighting) for "counts as a real
        // bond". Checked both directions via RealFriendOpinionOf so a
        // one-sided crush doesn't count as a mutual friendship, and using the
        // memory-based helper (not raw OpinionOf) so a passive/situational
        // bonus (e.g. a beauty-related trait) can't inflate the count.
        // Internal (not private) - PF_WandererTracker.CountStrongFriendsInColony
        // reuses the same bar (its own version takes an explicit originMap
        // since a resolving wanderer is already off the map, unlike a colonist
        // still spawned when TryFindRequestCandidate runs).
        internal const float StrongFriendOpinionThreshold = 40f;

        // How many close, mutual friendships this colonist has in their own
        // colony right now - used by TryFindRequestCandidate below to weigh a
        // visiting relation/friend against what the colonist would actually be
        // leaving behind. Per user request: a colonist shouldn't be offered
        // the choice to abandon several real friends in the colony just
        // because a visiting caravan happens to include one person they like -
        // that's the reverse of the family-faction weighting fix in
        // PF_WandererTracker (leaving should be biased toward strong ties, not
        // away from them).
        internal static int CountStrongFriendsInColony(Pawn pawn)
        {
            if (pawn.relations == null || pawn.Map == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Pawn other in pawn.Map.mapPawns.FreeColonistsSpawned)
            {
                if (other == pawn || other.Dead || other.relations == null)
                {
                    continue;
                }

                if (RealFriendOpinionOf(pawn, other) >= StrongFriendOpinionThreshold
                    && RealFriendOpinionOf(other, pawn) >= StrongFriendOpinionThreshold)
                {
                    count++;
                }
            }

            return count;
        }

        // A former partner is, by definition, no longer a reason to leave the
        // colony for - excluded here so an ex-spouse/ex-lover visiting never
        // outranks (or substitutes for) an actual current relation as the
        // reason a colonist wants to join a visiting faction.
        private static readonly HashSet<PawnRelationDef> ExcludedFromJoinRequest = new HashSet<PawnRelationDef>
        {
            PawnRelationDefOf.ExSpouse,
            PawnRelationDefOf.ExLover,
        };

        // Family outranks a plain friendship by default (see bestImportance's
        // relation.def.importance ranking below), but a colonist doesn't
        // automatically want to leave with a relative they dislike - e.g. a
        // misogynist colonist's opinion of a sister can be negative purely
        // from a trait, despite Sibling's normally-positive stock importance.
        // A relative only wins here if the colonist's actual opinion of them
        // isn't negative; otherwise we fall through to the best close-friend
        // visitor (opinion-based, no formal relation required) instead.
        // Internal (not private) - PF_WandererTracker.FindFamilyFactionCandidates
        // reuses the same threshold for the same reason (a negative-opinion
        // relative shouldn't bias faction choice either).
        internal const float MinRelativeOpinionToFollow = 0f;

        // Pawn.relations.OpinionOf(other) sums formal relation offsets AND
        // *every* social thought, including purely situational/passive ones
        // (e.g. a beauty-related trait's social effect) that say nothing about
        // an actual bond between these two specific pawns - a beautiful pawn
        // gets an inflated OpinionOf from everyone, "friend" or not. Per user
        // request (confirmed case: a colonist favored leaving several real
        // friends behind for one distant relation, because raw OpinionOf
        // treated a passive bonus the same as an earned one), "is this a real
        // friendship" checks should use this instead of raw OpinionOf.
        //
        // Sums formal relation.opinionOffset (Sibling/Spouse/etc - an actual
        // relation, not a passive trait) plus only the *memory* social
        // thoughts targeting `other` (RimWorld.Thought_MemorySocial and
        // subclasses implementing ISocialThought - GotVisited, SharedBed,
        // conversation-based thoughts, etc: things that happened between
        // these two pawns specifically). Deliberately excludes
        // SituationalThoughtHandler's output (what OpinionOf's
        // TotalOpinionOffset(other) also includes) since situational thoughts
        // are recomputed from current passive state (beauty, nudity, room
        // sharing) rather than an accumulated history with this specific pawn.
        internal static int RealFriendOpinionOf(Pawn pawn, Pawn other)
        {
            if (pawn?.relations == null || other == null || !other.RaceProps.Humanlike || pawn == other || pawn.Dead)
            {
                return 0;
            }

            int total = 0;
            foreach (PawnRelationDef relationDef in PawnRelationUtility.GetRelations(pawn, other))
            {
                total += relationDef.opinionOffset;
            }

            if (pawn.needs?.mood != null)
            {
                foreach (Thought_Memory memory in pawn.needs.mood.thoughts.memories.Memories)
                {
                    if (memory is ISocialThought socialThought && socialThought.OtherPawn() == other)
                    {
                        total += Mathf.RoundToInt(socialThought.OpinionOffset());
                    }
                }
            }

            return total;
        }

        private static Pawn FindClosestRelation(Pawn colonist, List<Pawn> visitors)
        {
            float minImportance = PeacefulFarewellMod.Settings.minRelationImportance;
            Pawn bestRelative = null;
            float bestImportance = -1f;
            Pawn bestFriend = null;
            float bestFriendOpinion = CloseFriendOpinionThreshold;

            foreach (Pawn visitor in visitors)
            {
                if (!visitor.RaceProps.Humanlike || visitor.Dead)
                {
                    continue;
                }

                bool hasFormalRelation = false;

                // PawnRelationUtility.GetRelations, not just DirectRelations -
                // Sibling (and some other kinships) is a virtual/inferred relation
                // computed from shared parents, not always a physical DirectRelation
                // entry on either pawn. Scanning DirectRelations alone silently
                // missed a colonist's sibling visiting with another faction - see
                // the matching comment on GetLovedOneCloseness below for the same
                // gap found earlier in the wanderlust-return notify.
                foreach (PawnRelationDef relationDef in PawnRelationUtility.GetRelations(colonist, visitor))
                {
                    if (ExcludedFromJoinRequest.Contains(relationDef))
                    {
                        continue;
                    }
                    hasFormalRelation = true;
                    if (relationDef.importance < minImportance)
                    {
                        continue;
                    }
                    if (relationDef.importance > bestImportance
                        && colonist.relations.OpinionOf(visitor) >= MinRelativeOpinionToFollow)
                    {
                        bestImportance = relationDef.importance;
                        bestRelative = visitor;
                    }
                }

                // Only counts as a "friend" candidate if there's no formal
                // relation at all - an actively disliked relative shouldn't
                // also get double-checked against the friend threshold using
                // the same relation.
                if (!hasFormalRelation)
                {
                    float opinion = colonist.relations.OpinionOf(visitor);
                    if (opinion >= bestFriendOpinion)
                    {
                        bestFriendOpinion = opinion;
                        bestFriend = visitor;
                    }
                }
            }

            return bestRelative ?? bestFriend;
        }

        public static void StartJoinJob(Pawn colonist, Pawn relatedPawn, Lord visitingLord, Faction targetFaction)
        {
            // Captured before SetFaction(null) below wipes ownership, so
            // JobDriver_JoinVisitingFaction's room-visit toil can still find
            // the pawn's own bed after they're already faction-less. Carried
            // via targetB (targetA is already relatedPawn) since it's part of
            // vanilla Job and survives save/load the same way targetA/lord do.
            Building_Bed ownedBed = colonist.ownership?.OwnedBed;

            // targetFaction (and vanilla's own Job.lord field) travel on the Job
            // itself instead of a side dictionary, so they survive save/load via
            // Pawn_JobTracker's normal job scribing - a plain static dictionary here
            // would be lost on reload and leave a mid-departure pawn factionless.
            PF_Job_JoinVisitingFaction job = new PF_Job_JoinVisitingFaction(PF_JobDefOf.PF_JoinVisitingFaction, relatedPawn, targetFaction)
            {
                lord = visitingLord,
                locomotionUrgency = LocomotionUrgency.Walk,
                targetB = ownedBed
            };

            // Cleared here, before the job starts - see the matching comment in
            // StartLeaveOutOfLonelinessJob for why this can no longer happen
            // from inside the job's own follow toil (SetFaction force-ends
            // whatever job is currently running).
            colonist.SetFaction(null);

            bool tookJob = colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"StartJoinJob for {colonist.LabelShort}: TryTakeOrderedJob={tookJob}, CurJobDef right after={colonist.CurJobDef?.defName ?? "null"}");
            }
            if (!tookJob)
            {
                // Something with higher priority (a mental break, medical emergency,
                // forced job, etc.) blocked the job from actually starting. Faction
                // was already cleared above, so it needs restoring here too.
                colonist.SetFaction(Faction.OfPlayer);
                SendDepartureFailedLetter(colonist);
                return;
            }

            TrySendBondedAnimalAlong(colonist);
        }

        public static void SendDeniedThought(Pawn colonist)
        {
            if (colonist.needs?.mood != null)
            {
                colonist.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_RequestToLeaveDenied);
            }
        }

        public static void SendJoinedLetter(Pawn colonist, Faction targetFaction)
        {
            string label = "PF_LetterLabel".Translate();
            TaggedString text = PF_Text.Variant("PF_LetterText", 4, colonist.Named("PAWN"), targetFaction.Name);
            Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.NeutralEvent);
        }

        public static void ApplyJoinVisitingFactionThoughts(Pawn departing)
        {
            if (departing.needs?.mood != null)
            {
                departing.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_ReunitedWithLovedOne);
            }

            Map map = departing.Map;
            if (map == null || departing.relations == null)
            {
                return;
            }

            foreach (DirectPawnRelation relation in departing.relations.DirectRelations)
            {
                Pawn other = relation.otherPawn;
                if (other == null || other.Dead || !other.IsFreeColonist || other.Map != map)
                {
                    continue;
                }
                if (other.needs?.mood == null)
                {
                    continue;
                }

                other.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_WillMissColonist, departing);
            }
        }

        public static bool HasNotLikedHereThought(Pawn pawn)
        {
            return pawn.needs?.mood != null
                && pawn.needs.mood.thoughts.memories.GetFirstMemoryOfDef(PF_ThoughtDefOf.PF_NotLikedHere) != null;
        }

        public static void GiveNotLikedHereThought(Pawn pawn)
        {
            if (pawn.needs?.mood == null)
            {
                return;
            }

            // Marked permanent so it never expires on its own - we add/remove it
            // ourselves whenever the loneliness condition starts/stops holding,
            // instead of relying on a very long durationDays (which would show
            // an absurd "expires in ~100 years" line in the Needs tab).
            Thought_Memory thought = (Thought_Memory)ThoughtMaker.MakeThought(PF_ThoughtDefOf.PF_NotLikedHere);
            thought.permanent = true;
            pawn.needs.mood.thoughts.memories.TryGainMemory(thought);
        }

        public static void RemoveNotLikedHereThought(Pawn pawn)
        {
            if (pawn.needs?.mood != null)
            {
                pawn.needs.mood.thoughts.memories.RemoveMemoriesOfDef(PF_ThoughtDefOf.PF_NotLikedHere);
            }
        }

        public static void StartLeaveOutOfLonelinessJob(Pawn colonist)
        {
            // Captured before SetFaction(null) below wipes ownership, so
            // JobDriver_LeaveOutOfLoneliness's room-visit toil can still find
            // the pawn's own bed after they're already faction-less.
            Building_Bed ownedBed = colonist.ownership?.OwnedBed;

            // Snapshotted before SetFaction(null) - OpinionOf includes vanilla
            // social thoughts that are conditional on the subject still being a
            // colonist (e.g. ThoughtWorkers gated on pawn.IsColonist). Calling
            // OpinionOf after SetFaction(null) silently drops those components,
            // which was observed pulling a strongly negative opinion (-40) up to
            // >= 0 by the time NotifyColonyAboutLonelinessDeparture checked it -
            // relief/pity was keying off a distorted, post-faction-loss number.
            Dictionary<Pawn, int> opinionsBeforeDeparture = SnapshotColonyOpinions(colonist);

            Job job = JobMaker.MakeJob(PF_JobDefOf.PF_LeaveOutOfLoneliness);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            job.targetA = ownedBed;

            // Cleared here, before the job starts, rather than from inside the
            // job's own exit toil - Pawn.SetFaction ends with
            // ClearMind_NewTemp() -> jobs.StopAll(), which force-ends whatever
            // job is currently running. Calling it from inside this job's own
            // initAction was silently killing the job the instant it started
            // (JobCondition.InterruptForced, no exception, no clean way to
            // detect it short of hooking Toil.AddFinishAction) - the pawn would
            // then get picked up by vanilla's faction-less-pawn AI and wander
            // off the map on its own, without JobDriver_LeaveOutOfLoneliness's
            // finish toil ever running, so BecomeWanderer/Register() never
            // fired and PF_WandererTracker never learned about them.
            colonist.SetFaction(null);

            bool tookJob = colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"StartLeaveOutOfLonelinessJob for {colonist.LabelShort}: TryTakeOrderedJob={tookJob}, CurJobDef right after={colonist.CurJobDef?.defName ?? "null"}");
            }
            if (!tookJob)
            {
                // Faction was already cleared above - something with higher
                // priority (mental break, medical emergency, forced job, etc.)
                // blocked the job from actually starting, which would otherwise
                // leave this colonist a faction-less pawn on the player's map
                // with no departure job running at all.
                colonist.SetFaction(Faction.OfPlayer);
                SendDepartureFailedLetter(colonist);
                return;
            }
            // Applied immediately on acceptance rather than when the pawn
            // actually reaches the map edge, since the walk can be interrupted
            // and colonists shouldn't have to wait to react to the departure.
            NotifyColonyAboutLonelinessDeparture(colonist, opinionsBeforeDeparture);
            TrySendBondedAnimalAlong(colonist);
        }

        public static void SendLonelinessDeniedThought(Pawn colonist)
        {
            RemoveNotLikedHereThought(colonist);

            if (colonist.needs?.mood != null)
            {
                colonist.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_LonelinessDenied);
            }
        }

        public static void BecomeWanderer(Pawn pawn)
        {
            // Captured before DeSpawn clears it - PF_WandererTracker needs to know
            // which colony this pawn actually left so it can later judge their
            // relations against the colonists who knew them, not every player map.
            Map originMap = pawn.Map;

            // Relief/pity thoughts for the rest of the colony are applied in
            // StartLeaveOutOfLonelinessJob, right on acceptance.
            RemoveNotLikedHereThought(pawn);
            if (pawn.needs?.mood != null)
            {
                pawn.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_LonelinessExcitedToLeave);
            }

            // Captured before SetFaction(null)/PassToWorld - if this pawn ever
            // returns, ReturnToColony's SetFaction(Faction.OfPlayer) would
            // otherwise reset work priorities and schedule to vanilla defaults.
            PF_GameComponent.SaveWorkAndSchedule(pawn);

            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }

            // Faction was already cleared in StartLeaveOutOfLonelinessJob, right when
            // the player accepted the request.
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            Current.Game.World.GetComponent<PF_WandererTracker>().Register(pawn, PF_WandererReason.Loneliness, originMap);
        }

        public static List<Pawn> FindWanderlustCandidates(Map map)
        {
            List<Pawn> options = new List<Pawn>();

            if (map == null || !map.IsPlayerHome)
            {
                return options;
            }

            foreach (Pawn colonist in map.mapPawns.FreeColonistsSpawned)
            {
                if (IsWanderlustEligible(colonist) && !PF_GameComponent.IsWanderlustRequestOnCooldown(colonist))
                {
                    options.Add(colonist);
                }
            }

            return options;
        }

        private static bool IsWanderlustEligible(Pawn pawn)
        {
            if (!IsEligibleColonist(pawn))
            {
                return false;
            }
            if (pawn.ageTracker == null)
            {
                return false;
            }
            // Just settled back in from a previous Wanderlust journey - give
            // them a real break (see WanderlustReturnCooldownTicks) before
            // they can be picked as a candidate again.
            if (PF_GameComponent.IsWanderlustReturnOnCooldown(pawn))
            {
                return false;
            }

            float age = pawn.ageTracker.AgeBiologicalYearsFloat;
            return age >= 13f && age < 18f;
        }

        public static void StartLeaveOutOfWanderlustJob(Pawn colonist)
        {
            // Captured before SetFaction(null) below wipes ownership, so
            // JobDriver_LeaveOutOfWanderlust's room-visit toil can still find
            // the pawn's own bed after they're already faction-less.
            Building_Bed ownedBed = colonist.ownership?.OwnedBed;

            Job job = JobMaker.MakeJob(PF_JobDefOf.PF_LeaveOutOfWanderlust);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            job.targetA = ownedBed;

            // Cleared here, before the job starts - see the matching comment in
            // StartLeaveOutOfLonelinessJob for why this can no longer happen
            // from inside the job's own exit toil (SetFaction force-ends
            // whatever job is currently running).
            colonist.SetFaction(null);

            bool tookJob = colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"StartLeaveOutOfWanderlustJob for {colonist.LabelShort}: TryTakeOrderedJob={tookJob}, CurJobDef right after={colonist.CurJobDef?.defName ?? "null"}");
            }
            if (!tookJob)
            {
                // Faction was already cleared above - see the matching comment
                // in StartLeaveOutOfLonelinessJob for why this restore is needed.
                colonist.SetFaction(Faction.OfPlayer);
                SendDepartureFailedLetter(colonist);
                return;
            }

            TrySendBondedAnimalAlong(colonist);
        }

        // Finds a spawned animal on the colonist's map that is bonded to them
        // (RimWorld's Bond relation is stored on the animal, pointing at its
        // owner). Only one bonded animal is ever expected in practice, but if
        // several exist we just take the first.
        public static Pawn FindBondedAnimal(Pawn colonist)
        {
            Map map = colonist.Map;
            if (map == null || colonist.relations == null)
            {
                return null;
            }

            foreach (Pawn animal in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            {
                if (animal == colonist || !animal.RaceProps.Animal || animal.Dead || animal.relations == null)
                {
                    continue;
                }

                foreach (DirectPawnRelation relation in animal.relations.DirectRelations)
                {
                    if (relation.def == PawnRelationDefOf.Bond && relation.otherPawn == colonist)
                    {
                        return animal;
                    }
                }
            }

            return null;
        }

        // Sends a colonist's bonded animal after them: the animal gets a job to
        // follow the colonist to the map edge, purely cosmetic - the colonist's
        // own departure job never waits on it. If the animal isn't close enough
        // when the colonist actually leaves the map, it simply stays behind.
        public static void TrySendBondedAnimalAlong(Pawn colonist)
        {
            Pawn animal = FindBondedAnimal(colonist);
            if (animal == null || !animal.Spawned)
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"TrySendBondedAnimalAlong for {colonist.LabelShort}: no spawned bonded animal found.");
                }
                return;
            }

            Job job = JobMaker.MakeJob(PF_JobDefOf.PF_FollowDepartingOwner, colonist);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            bool tookJob = animal.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"TrySendBondedAnimalAlong for {colonist.LabelShort}: bonded animal={animal.LabelShort}, TryTakeOrderedJob={tookJob}, CurJobDef right after={animal.CurJobDef?.defName ?? "null"}");
            }
        }

        // Called from JobDriver_FollowDepartingOwner right as the owner leaves
        // the map, while the animal was still close enough to be considered
        // "with" them. Mirrors whatever just happened to the owner: joins the
        // owner's new faction if they joined one, otherwise becomes a
        // factionless wanderer alongside them.
        public static void SendBondedAnimalAfterOwner(Pawn animal, Pawn owner)
        {
            if (animal.Spawned)
            {
                animal.DeSpawn(DestroyMode.Vanish);
            }

            animal.SetFaction(owner.Faction);
            Find.WorldPawns.PassToWorld(animal, PawnDiscardDecideMode.KeepForever);

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Bonded animal {animal.LabelShort} followed {owner.LabelShort} out, faction={owner.Faction?.Name ?? "none"}");
            }
        }

        public static void SendDepartureFailedLetter(Pawn colonist)
        {
            Find.LetterStack.ReceiveLetter(
                "PF_DepartureFailedLabel".Translate(),
                "PF_DepartureFailedText".Translate(colonist.Named("PAWN")),
                LetterDefOf.NegativeEvent,
                colonist);
        }

        public static void SendWanderlustDeniedThought(Pawn colonist)
        {
            if (colonist.needs?.mood != null)
            {
                colonist.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_WanderlustDenied);
            }
        }

        public static void BecomeWanderlustWanderer(Pawn pawn)
        {
            // Captured before DeSpawn clears it - see BecomeWanderer for why.
            Map originMap = pawn.Map;

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"BecomeWanderlustWanderer for {pawn.LabelShort}: entering, originMap={originMap}, spawned={pawn.Spawned}, faction={pawn.Faction?.Name ?? "null"}");
            }

            // See BecomeWanderer for why this needs to happen before the pawn
            // loses its player faction.
            PF_GameComponent.SaveWorkAndSchedule(pawn);

            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }

            // Faction was already cleared in StartLeaveOutOfWanderlustJob, right when
            // the player accepted the request.
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            Current.Game.World.GetComponent<PF_WandererTracker>().Register(pawn, PF_WandererReason.Wanderlust, originMap);

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"BecomeWanderlustWanderer for {pawn.LabelShort}: registered with PF_WandererTracker, discarded={pawn.Discarded}");
            }
        }

        public static void NotifyFamilyAboutWanderlustReturn(Pawn returned)
        {
            Map map = returned.Map;
            if (map == null || returned.relations == null)
            {
                PF_Log.Message($"NotifyFamilyAboutWanderlustReturn: {returned.LabelShort} - skipped, map or relations null");
                return;
            }

            int notified = 0;

            // Mirror NotifyLovedOnesAboutLostWanderer: use GetLovedOneCloseness
            // (which checks PawnRelationUtility.GetRelations, not just
            // DirectRelations) so virtual relations like Sibling are caught,
            // and close friends without a formal relation get the mood boost too.
            foreach (Pawn other in map.mapPawns.FreeColonistsSpawned)
            {
                if (other == returned || other.needs?.mood == null)
                {
                    continue;
                }

                LovedOneCloseness closeness = GetLovedOneCloseness(other, returned);
                if (closeness == LovedOneCloseness.None)
                {
                    continue;
                }

                other.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_WanderlustFamilyReturned, returned);
                notified++;

                PF_Log.Message($"NotifyFamilyAboutWanderlustReturn: {other.LabelShort} ({closeness}) <- {returned.LabelShort} | thought={PF_ThoughtDefOf.PF_WanderlustFamilyReturned?.defName ?? "NULL"}");
            }

            PF_Log.Message($"NotifyFamilyAboutWanderlustReturn: {returned.LabelShort} returned, notified {notified} loved one(s)");
        }

        // "Good friend" opinion bar used elsewhere in vanilla RimWorld (e.g. the
        // relations tab coloring) - reused here so a close bond without a formal
        // family/partner relation still counts as a loved one.
        private const float CloseFriendOpinionThreshold = 40f;

        // Blood family (parent/child/sibling) shouldn't need "good friend" levels
        // of opinion to grieve/celebrate a wanderer - that's what silently dropped
        // parents below the friend threshold (confirmed by user: mother and father
        // got no thought because their opinion was under 40). Family still needs
        // a low bar though, so a relation that's actively hostile (e.g. an
        // estranged/hated parent) doesn't get a mood swing over someone they
        // don't care about.
        private const float FamilyOpinionThreshold = 10f;

        private enum LovedOneCloseness
        {
            None,
            Friend,
            Family,
            Partner,
        }

        private static readonly HashSet<PawnRelationDef> PartnerRelations = new HashSet<PawnRelationDef>
        {
            PawnRelationDefOf.Spouse,
            PawnRelationDefOf.Lover,
            PawnRelationDefOf.Fiance,
        };

        private static readonly HashSet<PawnRelationDef> FamilyRelations = new HashSet<PawnRelationDef>
        {
            PawnRelationDefOf.Parent,
            PawnRelationDefOf.Child,
            PawnRelationDefOf.Sibling,
        };

        private static LovedOneCloseness GetLovedOneCloseness(Pawn other, Pawn subject)
        {
            if (other.relations == null)
            {
                return LovedOneCloseness.None;
            }

            // PawnRelationUtility.GetRelations, not other.relations.DirectRelations -
            // Sibling (and some other kinships) is a *virtual*/inferred relation
            // computed from shared parents, not always a physical DirectRelation
            // entry on either pawn. Scanning DirectRelations alone silently missed
            // siblings entirely (confirmed by user: father/mother/siblings left
            // behind got no thought at all).
            bool isFamily = false;
            foreach (PawnRelationDef relationDef in PawnRelationUtility.GetRelations(other, subject))
            {
                if (PartnerRelations.Contains(relationDef))
                {
                    return LovedOneCloseness.Partner;
                }
                if (FamilyRelations.Contains(relationDef))
                {
                    isFamily = true;
                }
            }

            if (isFamily)
            {
                return other.relations.OpinionOf(subject) >= FamilyOpinionThreshold
                    ? LovedOneCloseness.Family
                    : LovedOneCloseness.None;
            }

            if (other.relations.OpinionOf(subject) >= CloseFriendOpinionThreshold)
            {
                return LovedOneCloseness.Friend;
            }

            return LovedOneCloseness.None;
        }

        // defName prefix per tier ("PF_WordNeverCame" + tier + variant index,
        // e.g. "PF_WordNeverCameFamily2") - avoids a PF_ThoughtDefOf field per
        // variant. Looked up once per tier and cached instead of a static field
        // per ThoughtDef, since [DefOf] only works for exact fixed field names.
        private const int WordNeverCameVariantCount = 5;

        private static readonly Dictionary<LovedOneCloseness, ThoughtDef[]> WordNeverCameVariantsByTier =
            new Dictionary<LovedOneCloseness, ThoughtDef[]>();

        private static ThoughtDef[] GetWordNeverCameVariants(LovedOneCloseness closeness)
        {
            if (WordNeverCameVariantsByTier.TryGetValue(closeness, out ThoughtDef[] cached))
            {
                return cached;
            }

            ThoughtDef[] variants = new ThoughtDef[WordNeverCameVariantCount];
            for (int i = 0; i < WordNeverCameVariantCount; i++)
            {
                variants[i] = DefDatabase<ThoughtDef>.GetNamed($"PF_WordNeverCame{closeness}{i}");
            }

            WordNeverCameVariantsByTier[closeness] = variants;
            return variants;
        }

        // Called when a wanderer's fate resolves to "lost" - they joined a
        // hostile faction and the colony only ever gets the deliberately vague
        // "No word" letter. Family, partners and close friends left behind never
        // learn the truth either, just that word never came - partners take it
        // hardest, then blood family, then close friends, per closeness tier.
        public static void NotifyLovedOnesAboutLostWanderer(Pawn missing)
        {
            List<Map> maps = Find.Maps;
            int notified = 0;

            foreach (Map map in maps)
            {
                foreach (Pawn other in map.mapPawns.FreeColonistsSpawned)
                {
                    if (other == missing || other.needs?.mood == null)
                    {
                        continue;
                    }

                    LovedOneCloseness closeness = GetLovedOneCloseness(other, missing);
                    if (closeness == LovedOneCloseness.None)
                    {
                        continue;
                    }

                    ThoughtDef variant = GetWordNeverCameVariants(closeness).RandomElement();
                    other.needs.mood.thoughts.memories.TryGainMemory(variant, missing);
                    notified++;

                    PF_Log.Message($"NotifyLovedOnesAboutLostWanderer: {other.LabelShort} ({closeness}) <- {missing.LabelShort} | thought={variant?.defName ?? "NULL"}");
                }
            }

            PF_Log.Message($"NotifyLovedOnesAboutLostWanderer: {missing.LabelShort} lost, notified {notified} loved one(s) across {maps.Count} map(s)");
        }

        // defName prefix per tier ("PF_HeardTheyreSafe" + tier + variant index),
        // mirroring GetWordNeverCameVariants below.
        private const int HeardTheyreSafeVariantCount = 4;

        private static readonly Dictionary<LovedOneCloseness, ThoughtDef[]> HeardTheyreSafeVariantsByTier =
            new Dictionary<LovedOneCloseness, ThoughtDef[]>();

        private static ThoughtDef[] GetHeardTheyreSafeVariants(LovedOneCloseness closeness)
        {
            if (HeardTheyreSafeVariantsByTier.TryGetValue(closeness, out ThoughtDef[] cached))
            {
                return cached;
            }

            ThoughtDef[] variants = new ThoughtDef[HeardTheyreSafeVariantCount];
            for (int i = 0; i < HeardTheyreSafeVariantCount; i++)
            {
                variants[i] = DefDatabase<ThoughtDef>.GetNamed($"PF_HeardTheyreSafe{closeness}{i}");
            }

            HeardTheyreSafeVariantsByTier[closeness] = variants;
            return variants;
        }

        // Called when a wanderer's fate resolves to "settled with a friendly
        // faction" - the mirror-image of NotifyLovedOnesAboutLostWanderer. Family,
        // partners and close friends left behind get a mood boost from knowing
        // the wanderer made it somewhere safe, graded by closeness tier the same
        // way the negative version is.
        public static void NotifyLovedOnesAboutSafeWanderer(Pawn settled)
        {
            List<Map> maps = Find.Maps;
            int notified = 0;

            foreach (Map map in maps)
            {
                foreach (Pawn other in map.mapPawns.FreeColonistsSpawned)
                {
                    if (other == settled || other.needs?.mood == null)
                    {
                        continue;
                    }

                    LovedOneCloseness closeness = GetLovedOneCloseness(other, settled);
                    if (closeness == LovedOneCloseness.None)
                    {
                        continue;
                    }

                    ThoughtDef variant = GetHeardTheyreSafeVariants(closeness).RandomElement();
                    other.needs.mood.thoughts.memories.TryGainMemory(variant, settled);
                    notified++;

                    PF_Log.Message($"NotifyLovedOnesAboutSafeWanderer: {other.LabelShort} ({closeness}) <- {settled.LabelShort} | thought={variant?.defName ?? "NULL"}");
                }
            }

            PF_Log.Message($"NotifyLovedOnesAboutSafeWanderer: {settled.LabelShort} settled safely, notified {notified} loved one(s) across {maps.Count} map(s)");
        }

        // "Letter from afar" - a settled pawn (already resolved via
        // NotifyLovedOnesAboutSafeWanderer, now living their new life with a
        // friendly faction) occasionally writes back. Unlike the one-time
        // settle notification, this fires repeatedly over the course of the
        // save, so only one random loved one gets picked per letter rather
        // than notifying everyone at once - it reads as "a letter arrived,
        // addressed to someone in particular" rather than a colony-wide news
        // bulletin. Returns false (and does nothing) if no eligible loved one
        // is currently in the colony to receive it.
        public static bool TrySendLetterFromAfar(Pawn sender)
        {
            List<Map> maps = Find.Maps;
            List<(Pawn recipient, LovedOneCloseness closeness)> candidates = new List<(Pawn, LovedOneCloseness)>();

            foreach (Map map in maps)
            {
                foreach (Pawn other in map.mapPawns.FreeColonistsSpawned)
                {
                    if (other == sender || other.needs?.mood == null)
                    {
                        continue;
                    }

                    LovedOneCloseness closeness = GetLovedOneCloseness(other, sender);
                    if (closeness == LovedOneCloseness.None)
                    {
                        continue;
                    }

                    candidates.Add((other, closeness));
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            // Prefer the closest available loved one rather than picking
            // uniformly at random across every qualifying tier - a Partner
            // should always hear first if one's present, then Family, and
            // only Friend (the loosest tier, opinion-based rather than a
            // real relation) as a last resort. Within a tier, still pick
            // randomly among equally-close candidates (e.g. multiple family
            // members) rather than always the highest-opinion one.
            LovedOneCloseness bestTier = candidates.Max(c => c.closeness);
            List<Pawn> bestTierCandidates = candidates
                .Where(c => c.closeness == bestTier)
                .Select(c => c.recipient)
                .ToList();

            Pawn recipient = bestTierCandidates.RandomElement();
            LovedOneCloseness recipientCloseness = bestTier;

            GainMemoryWithSubject(recipient, PF_ThoughtDefOf.PF_LetterFromAfar, sender);

            Find.LetterStack.ReceiveLetter(
                "PF_LetterFromAfarLabel".Translate(),
                PF_Text.Variant("PF_LetterFromAfarText", 6, sender.Named("PAWN"), recipient.Named("RECIPIENT")),
                LetterDefOf.PositiveEvent,
                recipient);

            PF_Log.Message($"TrySendLetterFromAfar: {sender.LabelShort} sent a letter to {recipient.LabelShort} ({recipientCloseness})");
            return true;
        }

        private const float ApparelRefreshChancePerItem = 0.4f;
        private const int MaxApparelItemsRefreshed = 2;

        // Purely cosmetic flavor for a returning wanderer: RimWorld doesn't tick
        // world pawns, so nothing about their gear actually ages while they're
        // out there. Instead, on return, we just roll a chance to swap a couple
        // of worn items for freshly generated ones of the same ThingDef (so
        // layer/body coverage/quality band stay sensible) - selling the idea
        // that they traded, wore through, or replaced things on the road,
        // without tracking any real wear-and-tear over time.
        public static void RefreshApparelAfterJourney(Pawn pawn)
        {
            if (pawn.apparel == null || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            List<Apparel> candidates = pawn.apparel.WornApparel.ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            int swapped = 0;
            List<string> swappedLabels = new List<string>();

            foreach (Apparel oldApparel in candidates)
            {
                if (swapped >= MaxApparelItemsRefreshed)
                {
                    break;
                }
                if (!Rand.Chance(ApparelRefreshChancePerItem))
                {
                    continue;
                }

                Apparel newApparel = PawnApparelGenerator.GenerateApparelOfDefFor(pawn, oldApparel.def);
                if (newApparel == null)
                {
                    continue;
                }

                // Don't TryDrop - that spawns oldApparel on the ground right next
                // to the pawn, littering the map with "leftover" clothing from a
                // trip that was never actually tracked as worn-out. Just remove and
                // destroy it; the new item replaces it invisibly.
                pawn.apparel.Remove(oldApparel);
                oldApparel.Destroy();
                pawn.apparel.Wear(newApparel, dropReplacedApparel: true, locked: false);

                swapped++;
                swappedLabels.Add(oldApparel.def.defName);
            }

            if (PeacefulFarewellMod.Settings.debugMode && swapped > 0)
            {
                PF_Log.Message($"RefreshApparelAfterJourney: {pawn.LabelShort} came back with {swapped} item(s) replaced ({string.Join(", ", swappedLabels)})");
            }
        }

        // Grants a Thought_MemoryWithSubject-backed thought with subjectLabel set
        // to the subject's name. Needed because plain Thought_Memory has no
        // otherPawn field and never substitutes {0} on its own - only
        // Thought_MemorySocial does, and that shows in the Social tab instead of
        // Needs, which some of these thoughts (Needs-tab-visible on purpose)
        // can't use. def's thoughtClass must be PeacefulFarewell.Thought_MemoryWithSubject.
        public static void GainMemoryWithSubject(Pawn pawn, ThoughtDef def, Pawn subject)
        {
            Thought_MemoryWithSubject thought = (Thought_MemoryWithSubject)ThoughtMaker.MakeThought(def);
            thought.subjectLabel = subject.LabelShort;
            thought.subject = subject;
            pawn.needs.mood.thoughts.memories.TryGainMemory(thought);
        }

        // Captures each remaining colonist's opinion of departing while they're
        // still a colonist themselves - see the call site comment in
        // StartLeaveOutOfLonelinessJob for why this must happen before
        // SetFaction(null).
        private static Dictionary<Pawn, int> SnapshotColonyOpinions(Pawn departing)
        {
            Dictionary<Pawn, int> opinions = new Dictionary<Pawn, int>();
            Map map = departing.Map;
            if (map == null)
            {
                return opinions;
            }

            foreach (Pawn other in map.mapPawns.FreeColonistsSpawned)
            {
                if (other == departing || other.relations == null || other.needs?.mood == null)
                {
                    continue;
                }

                opinions[other] = other.relations.OpinionOf(departing);
            }

            return opinions;
        }

        private static void NotifyColonyAboutLonelinessDeparture(Pawn departing, Dictionary<Pawn, int> opinionsBeforeDeparture)
        {
            Map originMap = departing.MapHeld;
            if (opinionsBeforeDeparture.Count == 0)
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"NotifyColonyAboutLonelinessDeparture: {departing.LabelShort} has no snapshotted opinions, skipping notify entirely");
                }
                return;
            }

            int notified = 0;
            foreach (KeyValuePair<Pawn, int> entry in opinionsBeforeDeparture)
            {
                Pawn other = entry.Key;
                int opinion = entry.Value;

                // No dead zone: everyone who didn't like the departing colonist
                // (negative opinion) feels relief, everyone else feels a pang of
                // pity that they left.
                if (opinion < 0)
                {
                    GainMemoryWithSubject(other, PF_ThoughtDefOf.PF_ColonistLeftRelief, departing);
                }
                else
                {
                    GainMemoryWithSubject(other, PF_ThoughtDefOf.PF_ColonistLeftPity, departing);
                }
                notified++;
            }

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"NotifyColonyAboutLonelinessDeparture: {departing.LabelShort} departed from {originMap}, notified {notified} colonist(s) (relief/pity)");
            }
        }
    }
}
