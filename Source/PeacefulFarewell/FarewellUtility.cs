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

        // Shared by all departure jobs: a colonist with a bed first walks to
        // their room and lingers there 1-3 in-game hours (timer starts on
        // arrival), wandering around the room, before heading out. No bed -
        // straight to the exit toil. The bed is passed in because it was
        // captured before SetFaction wiped the pawn's ownership.
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
                    if (PF_Log.DebugMode)
                        PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: skipping room visit - bed={(bed == null ? "null" : bed.LabelShort)}, spawned={bed?.Spawned}, bedMap={bed?.Map}, pawnMap={pawn.Map}");
                    visit.actor.jobs.curDriver.ReadyForNextToil();
                    return;
                }

                IntVec3 sleepSpot = RestUtility.GetBedSleepingSlotPosFor(pawn, bed);
                if (PF_Log.DebugMode)
                    PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: heading to bed {bed.LabelShort} at {sleepSpot}, pawn currently at {pawn.Position}.");
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
                    if (PF_Log.DebugMode)
                        PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: arrived at bed, lingering until tick {arrivedTick} (current {Find.TickManager.TicksGame}).");
                    return;
                }

                if (Find.TickManager.TicksGame >= arrivedTick)
                {
                    if (PF_Log.DebugMode)
                        PF_Log.Message($"MakeRoomVisitToil for {pawn.LabelShort}: linger done, moving to next toil.");
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

            List<RequestCandidate> options = FindRequestCandidates(map);
            if (options.Count == 0)
            {
                return false;
            }

            candidate = options.RandomElement();
            return true;
        }

        // Every colonist/visitor pair on the map that qualifies for a join
        // request right now - one entry per (visiting lord, colonist).
        public static List<RequestCandidate> FindRequestCandidates(Map map)
        {
            List<RequestCandidate> options = new List<RequestCandidate>();

            if (map == null || !map.IsPlayerHome)
            {
                return options;
            }

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

                List<Pawn> visitors = lord.ownedPawns.ToList();
                foreach (Pawn colonist in map.mapPawns.FreeColonistsSpawned.ToList())
                {
                    if (!IsEligibleAdultColonist(colonist))
                    {
                        continue;
                    }
                    if (PF_GameComponent.IsJoinRequestOnCooldown(colonist) || HasPendingRequestLetter(colonist))
                    {
                        continue;
                    }
                    colonistsChecked++;

                    // Cheap check first - the colony-wide scans below run
                    // only for colonists who have someone among the visitors.
                    Pawn closePawn = FindClosestRelation(colonist, visitors);
                    if (closePawn == null)
                    {
                        continue;
                    }
                    if (HasFamilyInColony(colonist))
                    {
                        continue;
                    }
                    // Several real friends in the colony outweigh one
                    // visiting friend/relative - don't offer the choice.
                    if (CountStrongFriendsInColony(colonist, colonist.Map) >= 2)
                    {
                        continue;
                    }

                    options.Add(new RequestCandidate
                    {
                        colonist = colonist,
                        relatedPawn = closePawn,
                        visitingLord = lord,
                        targetFaction = lord.faction
                    });
                }
            }

            if (PF_Log.DebugMode)
                PF_Log.Message($"FindRequestCandidates on {map}: lordsTotal={lordsTotal}, lordsEligible={lordsEligible}, colonistsChecked={colonistsChecked}, options={options.Count}, minImportance={PeacefulFarewellMod.Settings.minRelationImportance}");

            return options;
        }

        // Vanilla visitors leave through LordToil_ExitMap, but trader
        // caravans use LordToil_ExitMapAndEscortCarriers, which isn't a
        // subclass of it - both mean "the visit is over".
        public static bool IsLordLeaving(Lord lord)
        {
            return lord.CurLordToil is LordToil_ExitMap || lord.CurLordToil is LordToil_ExitMapAndEscortCarriers;
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
            if (!(lord.LordJob is LordJob_VisitColony) && !(lord.LordJob is LordJob_TradeWithColony)
                && !IsHospitalityGuestLord(lord))
            {
                return false;
            }
            if (IsLordLeaving(lord))
            {
                return false;
            }
            return lord.ownedPawns.Any((Pawn p) => p.RaceProps.Humanlike);
        }

        // Hospitality guests run under the mod's own
        // Hospitality.LordJob_VisitColony - same class name as vanilla's, but
        // a separate type deriving straight from LordJob, so the vanilla `is`
        // check above never matched them (user report, 2026-10-21: a
        // colonist's son visiting as a Hospitality guest, "no candidate
        // found"). Matched by type name to avoid a hard dependency.
        private const string HospitalityGuestLordJobName = "Hospitality.LordJob_VisitColony";

        public static bool IsHospitalityGuestLord(Lord lord)
        {
            for (System.Type type = lord?.LordJob?.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName == HospitalityGuestLordJobName)
                {
                    return true;
                }
            }
            return false;
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

        // Whether this pawn already has one of our request letters sitting
        // undecided in the letter stack (e.g. postponed by the player). Such a
        // pawn must not be offered another request on top of it - otherwise a
        // later check stacks a duplicate (or a conflicting second departure)
        // for the same colonist. Deliberately NOT part of IsEligibleColonist:
        // the loneliness scan uses that too, and dropping a pawn from the scan
        // would strip their "not liked here" thought while the letter waits.
        public static bool HasPendingRequestLetter(Pawn pawn)
        {
            List<Letter> letters = Find.LetterStack?.LettersListForReading;
            if (letters == null)
            {
                return false;
            }

            foreach (Letter letter in letters)
            {
                if (letter is ChoiceLetter_PF_Request request && !request.decided && request.colonist == pawn)
                {
                    return true;
                }
            }

            return false;
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

                // GetRelations, not DirectRelations: also covers inferred
                // kinships like Sibling (computed from shared parents).
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

        // "Good friend" bar (relations tab parity) for a real, mutual bond.
        internal const float StrongFriendOpinionThreshold = 40f;

        // How many close, mutual friendships the pawn has among the free
        // colonists of `map` - checked both ways with RealFriendOpinionOf, so
        // neither a one-sided crush nor a passive bonus (e.g. beauty) counts.
        // Used to keep colonists with several real friends from being offered
        // a join request, and by PF_WandererTracker to dilute family-faction
        // weighting for a wanderer with strong bonds back home.
        internal static int CountStrongFriendsInColony(Pawn pawn, Map map)
        {
            if (pawn.relations == null || map == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Pawn other in map.mapPawns.FreeColonistsSpawned)
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

        // A former partner is no reason to leave the colony.
        private static readonly HashSet<PawnRelationDef> ExcludedFromJoinRequest = new HashSet<PawnRelationDef>
        {
            PawnRelationDefOf.ExSpouse,
            PawnRelationDefOf.ExLover,
        };

        // Family outranks friendship, but not a relative the colonist
        // dislikes (e.g. through a trait) - then the best close friend wins.
        // Also used by PF_WandererTracker's family-faction weighting.
        internal const float MinRelativeOpinionToFollow = 0f;

        // Opinion from an actual shared history: formal relation offsets plus
        // memory social thoughts about the other pawn (chats, insults, ...).
        // Unlike raw OpinionOf it leaves out situational thoughts (beauty,
        // nudity, ...), which inflate everyone's opinion of a pawn equally.
        // Use this for every "is this a real friendship" check.
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

                // GetRelations, not DirectRelations - see HasFamilyInColony.
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

                // Only visitors with no formal relation can count as friends,
                // judged by RealFriendOpinionOf (passive bonuses don't count).
                if (!hasFormalRelation)
                {
                    float opinion = RealFriendOpinionOf(colonist, visitor);
                    if (opinion >= bestFriendOpinion)
                    {
                        bestFriendOpinion = opinion;
                        bestFriend = visitor;
                    }
                }
            }

            return bestRelative ?? bestFriend;
        }

        // What kind of bond made a colonist want to leave with a visitor -
        // picks the join-request letter's wording and the matching reunion
        // mood thought, so "reunited with my sister" doesn't read like
        // "met a nice stranger".
        public enum JoinBond
        {
            Spouse,
            Fiance,
            Lover,
            Parent,
            Child,
            Sibling,
            OtherFamily,
            Friend
        }

        // Closest bond wins when several apply (e.g. a sibling who is also a
        // lover counts as the partner). Any other formal relation that
        // FindClosestRelation accepted (cousin, grandparent, ...) falls into
        // OtherFamily, with its def returned so the letter can name it.
        // Uses the same GetRelations/ExcludedFromJoinRequest rules as
        // FindClosestRelation so the two never disagree on "is this family".
        public static JoinBond GetJoinBond(Pawn colonist, Pawn related, out PawnRelationDef otherFamilyRelation)
        {
            otherFamilyRelation = null;
            if (colonist?.relations == null || related == null)
            {
                return JoinBond.Friend;
            }

            List<PawnRelationDef> relations = PawnRelationUtility.GetRelations(colonist, related)
                .Where((PawnRelationDef r) => !ExcludedFromJoinRequest.Contains(r))
                .ToList();

            if (relations.Contains(PawnRelationDefOf.Spouse))
            {
                return JoinBond.Spouse;
            }
            if (relations.Contains(PawnRelationDefOf.Fiance))
            {
                return JoinBond.Fiance;
            }
            if (relations.Contains(PawnRelationDefOf.Lover))
            {
                return JoinBond.Lover;
            }
            if (relations.Contains(PawnRelationDefOf.Parent))
            {
                return JoinBond.Parent;
            }
            if (relations.Contains(PawnRelationDefOf.Child))
            {
                return JoinBond.Child;
            }
            if (relations.Contains(PawnRelationDefOf.Sibling))
            {
                return JoinBond.Sibling;
            }
            if (relations.Count > 0)
            {
                otherFamilyRelation = relations.OrderByDescending((PawnRelationDef r) => r.importance).First();
                return JoinBond.OtherFamily;
            }

            return JoinBond.Friend;
        }

        // Number of "PF_JoinRequest_{bond}_Text{n}" variants per bond in the
        // Keyed files - must match every language's file.
        private static int JoinRequestVariantCount(JoinBond bond)
        {
            return bond == JoinBond.Friend ? 4 : 3;
        }

        public static TaggedString MakeJoinRequestLabel(JoinBond bond)
        {
            switch (bond)
            {
                case JoinBond.Spouse:
                case JoinBond.Fiance:
                case JoinBond.Lover:
                    return "PF_JoinRequestLabel_Partner".Translate();
                case JoinBond.Friend:
                    return "PF_JoinRequestLabel".Translate();
                default:
                    return "PF_JoinRequestLabel_Family".Translate();
            }
        }

        // Argument order is part of the Keyed contract: PAWN, RELATED, {2} =
        // faction name, RELATION = vanilla relation label (OtherFamily only,
        // empty otherwise). The shared "what happens if you accept/refuse"
        // paragraph is a separate key so every bond's variants stay short.
        // afterTalks: the decision came out of PF_VisitMeeting's talks (not a
        // debug-forced letter), so the letter says it wasn't a sudden whim.
        public static TaggedString MakeJoinRequestText(Pawn colonist, Pawn related, Faction faction, JoinBond bond, PawnRelationDef otherFamilyRelation, bool afterTalks = false)
        {
            string relationLabel = otherFamilyRelation != null ? otherFamilyRelation.GetGenderSpecificLabel(related) : "";
            TaggedString body = PF_Text.Variant("PF_JoinRequest_" + bond + "_Text", JoinRequestVariantCount(bond),
                colonist.Named("PAWN"), related.Named("RELATED"), faction.Name, relationLabel.Named("RELATION"));
            if (afterTalks)
            {
                body += "\n\n" + PF_Text.Variant("PF_JoinRequestAfterTalks", 3, colonist.Named("PAWN"), related.Named("RELATED"));
            }
            TaggedString footer = "PF_JoinRequestFooter".Translate(colonist.Named("PAWN"), related.Named("RELATED"), faction.Name);
            return body + "\n\n" + footer;
        }

        // ---- Visit talks (see PF_VisitMeeting) ----

        public enum VisitTalkOutcome
        {
            None,
            Warm,
            Invited,
            Snub
        }

        // How strongly the talks, bond, mood and colony ties have to pull
        // before the colonist asks to leave - see ComputeVisitPull.
        internal const float VisitPullThreshold = 40f;

        // A colonist's own opinion bar for "someone here I'd miss" when
        // weighing what they'd leave behind. Lower than the 40 "strong
        // friend" bar - two of those already exclude a colonist from the
        // candidate pool outright (see FindRequestCandidates).
        private const float VisitColonyFriendOpinion = 20f;
        private const int VisitColonyFriendsCap = 5;

        // Jobs a colonist is never pulled away from for a talk, even when
        // vanilla would let them be interrupted casually.
        private static readonly HashSet<JobDef> NeverInterruptForVisitTalk = new HashSet<JobDef>
        {
            JobDefOf.TendPatient,
            JobDefOf.Rescue,
            JobDefOf.BeatFire,
            JobDefOf.ExtinguishSelf,
            JobDefOf.Capture,
            JobDefOf.Arrest,
            JobDefOf.TakeWoundedPrisonerToBed,
            JobDefOf.Ingest,
            JobDefOf.LayDown,
            JobDefOf.Lovin,
            JobDefOf.Flee,
            JobDefOf.FleeAndCower,
            JobDefOf.TradeWithPawn,
        };

        // The visitors are still here, the colonist is still ours and on the
        // same map, and neither side has died or wandered off for good.
        internal static bool IsVisitMeetingStillPossible(PF_VisitMeeting meeting)
        {
            Pawn colonist = meeting.colonist;
            Pawn relative = meeting.relative;
            Lord lord = meeting.lord;

            if (colonist == null || colonist.Dead || !colonist.Spawned || colonist.Faction != Faction.OfPlayer || IsRunningFarewellJob(colonist))
            {
                return false;
            }
            if (relative == null || relative.Dead || !relative.Spawned || relative.Map != colonist.Map)
            {
                return false;
            }
            if (lord == null || lord.lordManager == null || !lord.lordManager.lords.Contains(lord)
                || lord.Map != colonist.Map || !lord.ownedPawns.Contains(relative) || IsLordLeaving(lord))
            {
                return false;
            }
            return true;
        }

        // Whether the colonist can be pulled into a talk right now. Talks may
        // interrupt ordinary work (otherwise they'd rarely happen during a
        // one-day caravan visit), but never sleep, meals, emergencies,
        // anything the player ordered, or while raiders are around.
        // `forced` (debug "Hurry visitor talks") skips the soft checks -
        // schedule, needs, current job, lords, threats - and keeps only
        // what would make the talk physically impossible. `reason` says
        // what blocked it, for the debug log.
        public static bool CanStartVisitTalk(Pawn colonist, Pawn relative, bool forced, out string reason)
        {
            Map map = colonist.Map;
            if (map == null || relative.Map != map)
            {
                reason = "not on the same map";
                return false;
            }
            if (colonist.Downed || colonist.Drafted || colonist.InMentalState || !colonist.Awake())
            {
                reason = $"colonist downed={colonist.Downed}, drafted={colonist.Drafted}, mentalState={colonist.InMentalState}, awake={colonist.Awake()}";
                return false;
            }
            if (relative.Downed || relative.InMentalState || !relative.Awake() || relative.interactions == null)
            {
                reason = $"visitor downed={relative.Downed}, mentalState={relative.InMentalState}, awake={relative.Awake()}, interactions={relative.interactions != null}";
                return false;
            }

            if (!forced)
            {
                if (colonist.InBed())
                {
                    reason = "colonist in bed";
                    return false;
                }
                // Rituals, parties, caravans being formed...
                if (colonist.GetLord() != null)
                {
                    reason = $"colonist in lord {colonist.GetLord().LordJob?.GetType().Name ?? "null"}";
                    return false;
                }
                if (colonist.carryTracker?.CarriedThing != null)
                {
                    reason = $"colonist carrying {colonist.carryTracker.CarriedThing.LabelShort}";
                    return false;
                }
                if (colonist.timetable?.CurrentAssignment == TimeAssignmentDefOf.Sleep)
                {
                    reason = "sleep time in schedule";
                    return false;
                }
                if (colonist.needs?.food != null && colonist.needs.food.CurCategory >= HungerCategory.UrgentlyHungry)
                {
                    reason = "colonist urgently hungry";
                    return false;
                }
                if (colonist.needs?.rest != null && colonist.needs.rest.CurCategory >= RestCategory.VeryTired)
                {
                    reason = "colonist very tired";
                    return false;
                }

                Job curJob = colonist.CurJob;
                if (curJob != null && (curJob.playerForced || !curJob.def.casualInterruptible || NeverInterruptForVisitTalk.Contains(curJob.def)))
                {
                    reason = $"colonist job {curJob.def.defName} (playerForced={curJob.playerForced}, casualInterruptible={curJob.def.casualInterruptible})";
                    return false;
                }

                if (GenHostility.AnyHostileActiveThreatToPlayer(map))
                {
                    reason = "hostile threat on the map";
                    return false;
                }
            }

            if (!colonist.CanReach(relative, PathEndMode.Touch, Danger.Some))
            {
                reason = "visitor not reachable";
                return false;
            }
            reason = null;
            return true;
        }

        // Interrupts the colonist's current work (resumed afterwards).
        public static void StartVisitTalk(Pawn colonist, Pawn relative)
        {
            Job job = JobMaker.MakeJob(PF_JobDefOf.PF_TalkWithVisitingRelative, relative);
            colonist.jobs.StartJob(job, JobCondition.InterruptForced, null, resumeCurJobAfterwards: true);
        }

        // Has the visitor stop and face the colonist while they talk - only
        // when they're doing something unimportant (wandering, standing
        // around), never over a player-ordered or non-interruptible job.
        public static void TryMakeVisitorListen(Pawn relative, Pawn colonist)
        {
            if (relative == null || relative.Downed || relative.InMentalState || !relative.Awake() || relative.jobs == null)
            {
                return;
            }
            Job curJob = relative.CurJob;
            if (curJob != null && (curJob.playerForced || !curJob.def.casualInterruptible || curJob.def == PF_JobDefOf.PF_ListenToVisitingRelative))
            {
                return;
            }

            Job listen = JobMaker.MakeJob(PF_JobDefOf.PF_ListenToVisitingRelative, colonist);
            listen.expiryInterval = GenDate.TicksPerHour;
            relative.jobs.StartJob(listen, JobCondition.InterruptForced, null, resumeCurJobAfterwards: true);
        }

        // Rolled once per talk. The visitor's own opinion of the colonist
        // decides how likely they are to brush them off (an abrasive visitor
        // more so, a kind one hardly ever); otherwise a visitor who likes the
        // colonist is more likely to invite them along.
        public static VisitTalkOutcome RollVisitTalkOutcome(Pawn colonist, Pawn relative)
        {
            float relativeOpinion = relative?.relations != null ? relative.relations.OpinionOf(colonist) : 0f;

            float snubChance = Mathf.Clamp(0.15f - relativeOpinion / 200f, 0.03f, 0.5f);
            if (relative?.story?.traits != null)
            {
                if (relative.story.traits.HasTrait(TraitDefOf.Abrasive))
                {
                    snubChance = Mathf.Min(snubChance * 2f, 0.6f);
                }
                if (relative.story.traits.HasTrait(TraitDefOf.Kind))
                {
                    snubChance *= 0.3f;
                }
            }
            if (Rand.Chance(snubChance))
            {
                return VisitTalkOutcome.Snub;
            }

            float inviteChance = relativeOpinion >= 20f ? 0.35f : 0.15f;
            return Rand.Chance(inviteChance) ? VisitTalkOutcome.Invited : VisitTalkOutcome.Warm;
        }

        public static float VisitTalkPoints(VisitTalkOutcome outcome)
        {
            switch (outcome)
            {
                case VisitTalkOutcome.Warm: return 10f;
                case VisitTalkOutcome.Invited: return 18f;
                case VisitTalkOutcome.Snub: return -25f;
                default: return 0f;
            }
        }

        private static float JoinBondPull(JoinBond bond)
        {
            switch (bond)
            {
                case JoinBond.Spouse: return 40f;
                case JoinBond.Fiance: return 35f;
                case JoinBond.Lover: return 30f;
                case JoinBond.Parent:
                case JoinBond.Child: return 25f;
                case JoinBond.Sibling: return 20f;
                case JoinBond.OtherFamily: return 10f;
                default: return 5f;
            }
        }

        // Decides the meeting once the last talk is done; >= VisitPullThreshold
        // means the colonist asks to leave. Made of:
        //  - bond: a spouse pulls much harder than a friend
        //  - the talks themselves (warm / invited / snubbed)
        //  - the colonist's real opinion of the visitor (RealFriendOpinionOf,
        //    so a vanilla insult or slight during the visit counts too, and
        //    so does the snub thought from our own talks)
        //  - mood: an unhappy colonist is easier to tempt away
        //  - people in the colony they'd miss
        // breakdown is for the debug log.
        public static float ComputeVisitPull(PF_VisitMeeting meeting, out string breakdown)
        {
            Pawn colonist = meeting.colonist;
            Pawn relative = meeting.relative;

            float bondPull = JoinBondPull(GetJoinBond(colonist, relative, out _));
            float opinionPull = (RealFriendOpinionOf(colonist, relative) - 30f) * 0.5f;
            float moodPull = colonist.needs?.mood != null ? (0.5f - colonist.needs.mood.CurLevel) * 60f : 0f;
            int colonyFriends = CountVisitColonyFriends(colonist);
            float colonyPull = -6f * colonyFriends;

            float total = bondPull + meeting.talkPoints + opinionPull + moodPull + colonyPull;
            breakdown = $"bond={bondPull:F0}, talks={meeting.talkPoints:F0} ({meeting.talksDone} talks, {meeting.snubs} snubs), opinion={opinionPull:F1}, mood={moodPull:F1}, colonyFriends={colonyFriends} ({colonyPull:F0}) => {total:F1} / {VisitPullThreshold:F0}";
            return total;
        }

        private static int CountVisitColonyFriends(Pawn colonist)
        {
            if (colonist.Map == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Pawn other in colonist.Map.mapPawns.FreeColonistsSpawned)
            {
                if (other == colonist || other.Dead)
                {
                    continue;
                }
                if (RealFriendOpinionOf(colonist, other) >= VisitColonyFriendOpinion && ++count >= VisitColonyFriendsCap)
                {
                    break;
                }
            }
            return count;
        }

        // The colonist stays (or the visitors left before the last talk) -
        // a small "good to see them" boost, unless they were snubbed.
        public static void GiveGoodToSeeVisitorThought(Pawn colonist, Pawn relative)
        {
            if (colonist?.needs?.mood == null || relative == null || colonist.Dead)
            {
                return;
            }
            GainMemoryWithSubject(colonist, PF_ThoughtDefOf.PF_GoodToSeeVisitor, relative);
        }

        public static void StartJoinJob(Pawn colonist, Pawn relatedPawn, Lord visitingLord, Faction targetFaction)
        {
            // Goodwill can change while the letter sits postponed - never hand
            // a colonist over to a faction that has turned hostile meanwhile.
            if (targetFaction == null || targetFaction.IsPlayer || targetFaction.HostileTo(Faction.OfPlayer))
            {
                SendDepartureFailedLetter(colonist);
                return;
            }

            // Captured before SetFaction below wipes ownership, for the
            // room-visit toil; carried in targetB (targetA is the relative).
            Building_Bed ownedBed = colonist.ownership?.OwnedBed;

            // Faction and lord travel on the Job itself, so they survive save/load.
            PF_Job_JoinVisitingFaction job = new PF_Job_JoinVisitingFaction(PF_JobDefOf.PF_JoinVisitingFaction, relatedPawn, targetFaction)
            {
                lord = visitingLord,
                targetB = ownedBed
            };

            // Joins the faction right away, while walking over - and before
            // the job starts, since SetFaction ends the pawn's current job.
            colonist.SetFaction(targetFaction);

            bool tookJob = colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PF_Log.DebugMode)
                PF_Log.Message($"StartJoinJob for {colonist.LabelShort}: TryTakeOrderedJob={tookJob}, CurJobDef right after={colonist.CurJobDef?.defName ?? "null"}");
            if (!tookJob)
            {
                // Something with higher priority (a mental break, medical emergency,
                // forced job, etc.) blocked the job from actually starting. Faction
                // was already changed above, so it needs restoring here too.
                colonist.SetFaction(Faction.OfPlayer);
                SendDepartureFailedLetter(colonist);
                return;
            }

            GiveReunionThought(colonist, relatedPawn);
            TrySendBondedAnimalAlong(colonist, unhurried: false);
        }

        private static ThoughtDef ReunionThoughtFor(JoinBond bond)
        {
            switch (bond)
            {
                case JoinBond.Spouse: return PF_ThoughtDefOf.PF_ReunitedSpouse;
                case JoinBond.Fiance: return PF_ThoughtDefOf.PF_ReunitedFiance;
                case JoinBond.Lover: return PF_ThoughtDefOf.PF_ReunitedLover;
                case JoinBond.Parent: return PF_ThoughtDefOf.PF_ReunitedParent;
                case JoinBond.Child: return PF_ThoughtDefOf.PF_ReunitedChild;
                case JoinBond.Sibling: return PF_ThoughtDefOf.PF_ReunitedSibling;
                case JoinBond.OtherFamily: return PF_ThoughtDefOf.PF_ReunitedFamily;
                default: return PF_ThoughtDefOf.PF_ReunitedFriend;
            }
        }

        // Granted on accept rather than at the map edge, so the player
        // actually sees the boost while the colonist walks over - the old
        // generic PF_ReunitedWithLovedOne was granted the instant they
        // despawned, where nobody could see it.
        private static void GiveReunionThought(Pawn colonist, Pawn relatedPawn)
        {
            if (colonist.needs?.mood == null || relatedPawn == null)
            {
                return;
            }

            JoinBond bond = GetJoinBond(colonist, relatedPawn, out _);
            GainMemoryWithSubject(colonist, ReunionThoughtFor(bond), relatedPawn);
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

        // A colonist who left with visitors becomes a settled pawn like a
        // wanderer who joined a faction - home settlement, presence on its
        // map, overview entry - but writes home only very rarely (see
        // PF_WandererTracker.RollNextLetterIntervalTicks). Registered while
        // still walking out with the visitors: PF_SettlementPresence ignores
        // spawned pawns, so nothing tries to place them anywhere until they've
        // actually left the map.
        public static void RegisterJoinedVisitors(Pawn pawn, Map originMap)
        {
            PF_WandererTracker tracker = PF_WandererTracker.Instance;
            if (tracker == null || tracker.IsSettled(pawn))
            {
                return;
            }
            tracker.RegisterSettled(pawn, originMap, Find.TickManager.TicksGame, viaVisitors: true);
        }

        // The departing pawn's own reunion thought is given earlier, in
        // StartJoinJob (see GiveReunionThought) - this only handles the
        // colonists left behind.
        public static void ApplyJoinVisitingFactionThoughts(Pawn departing)
        {
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
            // Captured before SetFaction(null) below wipes ownership.
            Building_Bed ownedBed = colonist.ownership?.OwnedBed;

            // Snapshotted before SetFaction(null) - some social thoughts only
            // apply while the subject is a colonist, so opinions read afterwards
            // are distorted (a -40 was seen turning into >= 0).
            Dictionary<Pawn, int> opinionsBeforeDeparture = SnapshotColonyOpinions(colonist);

            Job job = JobMaker.MakeJob(PF_JobDefOf.PF_LeaveOutOfLoneliness);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            job.targetA = ownedBed;

            // Cleared before the job starts, never from inside it: SetFaction
            // ends with jobs.StopAll(), which silently killed the job and let
            // the pawn walk off without ever being registered with the tracker.
            colonist.SetFaction(null);

            bool tookJob = colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PF_Log.DebugMode)
                PF_Log.Message($"StartLeaveOutOfLonelinessJob for {colonist.LabelShort}: TryTakeOrderedJob={tookJob}, CurJobDef right after={colonist.CurJobDef?.defName ?? "null"}");
            if (!tookJob)
            {
                // Something with higher priority blocked the job - undo the
                // faction change instead of leaving a factionless pawn behind.
                colonist.SetFaction(Faction.OfPlayer);
                SendDepartureFailedLetter(colonist);
                return;
            }
            // The colony reacts on acceptance, not at the map edge.
            NotifyColonyAboutLonelinessDeparture(colonist, opinionsBeforeDeparture);
            TrySendBondedAnimalAlong(colonist, unhurried: true);
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
            // Captured before DeSpawn clears it - the tracker judges relations
            // against the colony the pawn actually left.
            Map originMap = pawn.Map;

            // Relief/pity thoughts for the rest of the colony are applied in
            // StartLeaveOutOfLonelinessJob, right on acceptance.
            RemoveNotLikedHereThought(pawn);
            if (pawn.needs?.mood != null)
            {
                pawn.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_LonelinessExcitedToLeave);
            }

            // Restored if they ever return (SetFaction resets these to defaults).
            PF_GameComponent.SaveWorkAndSchedule(pawn);

            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }

            // Register BEFORE PassToWorld, which would otherwise hand a
            // factionless world pawn a random faction (see
            // Patch_NotifyPassedToWorld, which keys off IsTracked).
            PF_WandererTracker.Instance.Register(pawn, PF_WandererReason.Loneliness, originMap);

            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);
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
                if (IsWanderlustEligible(colonist) && !PF_GameComponent.IsWanderlustRequestOnCooldown(colonist) && !HasPendingRequestLetter(colonist))
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
            // Captured before SetFaction(null) below wipes ownership.
            Building_Bed ownedBed = colonist.ownership?.OwnedBed;

            Job job = JobMaker.MakeJob(PF_JobDefOf.PF_LeaveOutOfWanderlust);
            job.locomotionUrgency = LocomotionUrgency.Walk;
            job.targetA = ownedBed;

            // Before the job starts - see StartLeaveOutOfLonelinessJob.
            colonist.SetFaction(null);

            bool tookJob = colonist.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PF_Log.DebugMode)
                PF_Log.Message($"StartLeaveOutOfWanderlustJob for {colonist.LabelShort}: TryTakeOrderedJob={tookJob}, CurJobDef right after={colonist.CurJobDef?.defName ?? "null"}");
            if (!tookJob)
            {
                // Undo the faction change - see StartLeaveOutOfLonelinessJob.
                colonist.SetFaction(Faction.OfPlayer);
                SendDepartureFailedLetter(colonist);
                return;
            }

            TrySendBondedAnimalAlong(colonist, unhurried: true);
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
        // `unhurried` matches the owner's pace: the slow walk of a lonely or
        // wanderlust departure, or the game's default pace when joining
        // visitors.
        public static void TrySendBondedAnimalAlong(Pawn colonist, bool unhurried)
        {
            Pawn animal = FindBondedAnimal(colonist);
            if (animal == null || !animal.Spawned)
            {
                if (PF_Log.DebugMode)
                    PF_Log.Message($"TrySendBondedAnimalAlong for {colonist.LabelShort}: no spawned bonded animal found.");
                return;
            }

            Job job = JobMaker.MakeJob(PF_JobDefOf.PF_FollowDepartingOwner, colonist);
            if (unhurried)
            {
                job.locomotionUrgency = LocomotionUrgency.Walk;
            }
            bool tookJob = animal.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            if (PF_Log.DebugMode)
                PF_Log.Message($"TrySendBondedAnimalAlong for {colonist.LabelShort}: bonded animal={animal.LabelShort}, TryTakeOrderedJob={tookJob}, CurJobDef right after={animal.CurJobDef?.defName ?? "null"}");
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

            if (PF_Log.DebugMode)
                PF_Log.Message($"Bonded animal {animal.LabelShort} followed {owner.LabelShort} out, faction={owner.Faction?.Name ?? "none"}");
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

            if (PF_Log.DebugMode)
                PF_Log.Message($"BecomeWanderlustWanderer for {pawn.LabelShort}: entering, originMap={originMap}, spawned={pawn.Spawned}, faction={pawn.Faction?.Name ?? "null"}");

            // See BecomeWanderer for why this needs to happen before the pawn
            // loses its player faction.
            PF_GameComponent.SaveWorkAndSchedule(pawn);

            if (pawn.Spawned)
            {
                pawn.DeSpawn(DestroyMode.Vanish);
            }

            // Register BEFORE PassToWorld - see BecomeWanderer. Faction was already
            // cleared in StartLeaveOutOfWanderlustJob, right when the player accepted.
            PF_WandererTracker.Instance.Register(pawn, PF_WandererReason.Wanderlust, originMap);

            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            if (PF_Log.DebugMode)
                PF_Log.Message($"BecomeWanderlustWanderer for {pawn.LabelShort}: registered with PF_WandererTracker, discarded={pawn.Discarded}");
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

        // "Good friend" bar (relations tab parity) for a loved one with no
        // formal relation.
        private const float CloseFriendOpinionThreshold = 40f;

        // Blood family needs only a low bar - just not an estranged relative.
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

            // GetRelations, not DirectRelations - see HasFamilyInColony.
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

        // ThoughtDefs named "PF_WordNeverCame" + tier + index (e.g.
        // "PF_WordNeverCameFamily2"), looked up once per tier and cached.
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

        // A wanderer's fate resolved to "lost" (secretly joined a hostile
        // faction) - loved ones only learn that word never came, graded by
        // closeness tier.
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

        // "PF_HeardTheyreSafe" + tier + index, like GetWordNeverCameVariants.
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

        // A wanderer settled with a friendly faction - loved ones get a mood
        // boost, graded by closeness tier.
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

        // A settled pawn occasionally writes home to one loved one. Returns
        // false if no loved one is in the colony to receive it.
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

            // Closest tier first (partner, then family, then friend), random
            // within the tier.
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

        // Cosmetic: a returning wanderer swaps up to a couple of worn items
        // for fresh ones of the same def, as if replaced on the road.
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

                // Destroyed, not dropped - no leftover clothing on the map.
                pawn.apparel.Remove(oldApparel);
                oldApparel.Destroy();
                pawn.apparel.Wear(newApparel, dropReplacedApparel: true, locked: false);

                swapped++;
                swappedLabels.Add(oldApparel.def.defName);
            }

            if (PF_Log.DebugMode && swapped > 0)
                PF_Log.Message($"RefreshApparelAfterJourney: {pawn.LabelShort} came back with {swapped} item(s) replaced ({string.Join(", ", swappedLabels)})");
        }

        // A Needs-tab memory naming the subject (plain Thought_Memory can't, and
        // Thought_MemorySocial would show in the Social tab instead). The def's
        // thoughtClass must be PeacefulFarewell.Thought_MemoryWithSubject.
        public static void GainMemoryWithSubject(Pawn pawn, ThoughtDef def, Pawn subject)
        {
            Thought_MemoryWithSubject thought = (Thought_MemoryWithSubject)ThoughtMaker.MakeThought(def);
            thought.subjectLabel = subject.LabelShort;
            thought.subject = subject;
            pawn.needs.mood.thoughts.memories.TryGainMemory(thought);
        }

        // See StartLeaveOutOfLonelinessJob for why this runs before SetFaction(null).
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
                if (PF_Log.DebugMode)
                    PF_Log.Message($"NotifyColonyAboutLonelinessDeparture: {departing.LabelShort} has no snapshotted opinions, skipping notify entirely");
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

            if (PF_Log.DebugMode)
                PF_Log.Message($"NotifyColonyAboutLonelinessDeparture: {departing.LabelShort} departed from {originMap}, notified {notified} colonist(s) (relief/pity)");
        }
    }
}
