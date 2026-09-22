using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    public class PF_GameComponent : GameComponent
    {
        private const int CheckIntervalTicks = 2500;

        // Wanderlust eligibility spans a full 5-year age band (13-18, see
        // IsWanderlustEligible), so rolling wanderlustChancePerCheck on the
        // same hourly cadence as the other checks means a pawn accumulates
        // thousands of independent rolls over that window - even a "rare"
        // 0.5% setting then fires for almost every teenager within days of
        // turning 13 (1 - 0.005)^(hourly rolls over 5 years) rounds to ~0%
        // survival. Checking once per in-game day instead keeps the setting's
        // displayed percentage meaningful as a genuine per-roll rarity.
        private const int WanderlustCheckIntervalTicks = GenDate.TicksPerDay;
        private int nextWanderlustCheckTick = -1;

        // FindLonelyColonists is O(n^2) OpinionOf calls, and OpinionOf itself is
        // one of the more expensive vanilla calls (it walks the full social
        // thought list for the pair). With large colonies/many bases that adds
        // up to thousands of calls if run in a single tick, which shows up as a
        // visible freeze. Instead the scan is spread across many ticks, doing at
        // most this many OpinionOf evaluations per tick.
        private const int OpinionEvaluationsPerTick = 200;

        // How long a colonist who just returned from wandering off out of
        // loneliness is shielded from being picked as a loneliness candidate
        // again, giving the player a real window to fix their relations
        // instead of the request firing again almost immediately.
        private const int LonelinessReturnCooldownTicks = 5 * GenDate.TicksPerDay;

        // A colonist fresh out of the prison cell hasn't had time to build up
        // opinions with the rest of the colony yet, so a straight OpinionOf
        // scan can flag them as "lonely" within the first in-game hour purely
        // for lack of history, not because anyone dislikes them. Shield them
        // from being picked as a loneliness candidate for a while after
        // recruitment so relationships have a chance to actually form first.
        private const int RecruitedLonelinessCooldownTicks = 5 * GenDate.TicksPerDay;

        // How long a colonist is shielded from being offered the same kind of
        // request again after the player rejected it, matching the duration of
        // the "denied" thought that rejection grants them (see
        // ThoughtDefs_PeacefulFarewell.xml) - the request stops re-firing for
        // them for as long as they're still stewing over being told no.
        private const int JoinDeniedCooldownTicks = 4 * GenDate.TicksPerDay;
        private const int LonelinessDeniedCooldownTicks = 4 * GenDate.TicksPerDay;
        private const int WanderlustDeniedCooldownTicks = 3 * GenDate.TicksPerDay;

        // How long a colonist who just settled back in after a Wanderlust
        // journey is shielded from being picked as a Wanderlust candidate
        // again - 2 in-game years (GenDate.DaysPerYear = 60). Not permanent:
        // per user request, someone who's had time to miss the road again
        // should eventually be able to get the itch once more. In practice
        // IsWanderlustEligible's 13-18 age band often excludes them again
        // naturally before this even lapses - this cooldown mainly matters
        // for mods/scenarios that widen that band.
        private const int WanderlustReturnCooldownTicks = 2 * GenDate.DaysPerYear * GenDate.TicksPerDay;

        private Queue<Map> pendingLonelinessMaps = new Queue<Map>();
        private PF_LonelinessScan activeScan;
        private Dictionary<Pawn, int> lonelinessCooldownUntilTick = new Dictionary<Pawn, int>();
        private Dictionary<Pawn, int> joinDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
        private Dictionary<Pawn, int> lonelinessDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
        private Dictionary<Pawn, int> wanderlustDeniedCooldownUntilTick = new Dictionary<Pawn, int>();

        // Pawns who just settled back in after a Wanderlust journey - shielded
        // from being re-offered Wanderlust until the cooldown above lapses.
        private Dictionary<Pawn, int> wanderlustReturnCooldownUntilTick = new Dictionary<Pawn, int>();

        // Pawns who have EVER returned from a Wanderlust journey, at any point
        // in this save - permanent, never cleared. Unlike the cooldown above,
        // this doesn't gate eligibility at all; it's only consulted to pick a
        // different ("returning veteran", nostalgic) letter text variant when
        // such a pawn is offered Wanderlust again after their cooldown lapses.
        private HashSet<Pawn> everReturnedFromWanderlust = new HashSet<Pawn>();

        // Snapshot of a former colonist's work priorities and daily schedule,
        // taken right before they leave the colony (still a full colonist at
        // that point) and restored when/if they come back. Without this,
        // SetFaction(Faction.OfPlayer) on return re-initializes both to
        // vanilla defaults the same way a freshly recruited pawn would get,
        // wiping out whatever the player had configured for them.
        private Dictionary<Pawn, PF_WorkAndScheduleSnapshot> savedWorkAndSchedule = new Dictionary<Pawn, PF_WorkAndScheduleSnapshot>();

        // Per-save random offset added to TicksGame before the % CheckIntervalTicks
        // test in GameComponentTick, so the check's phase isn't pinned to
        // multiples of CheckIntervalTicks in absolute game time. Without this,
        // any save whose TicksGame happens to land on such a multiple (which
        // includes every exact day boundary, since a day is 60000 ticks - a
        // multiple of 2500) fires the check on the very first tick after
        // loading, every time. Rolled once and persisted so it stays stable
        // for the life of the save rather than re-randomizing (and briefly
        // double-firing near the boundary) on every load.
        private int checkPhaseOffset = -1;

        public PF_GameComponent(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref checkPhaseOffset, "checkPhaseOffset", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && checkPhaseOffset < 0)
            {
                checkPhaseOffset = Rand.Range(0, CheckIntervalTicks);
            }

            Scribe_Values.Look(ref nextWanderlustCheckTick, "nextWanderlustCheckTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && nextWanderlustCheckTick < 0)
            {
                nextWanderlustCheckTick = Find.TickManager.TicksGame + WanderlustCheckIntervalTicks;
            }

            Scribe_Collections.Look(ref lonelinessCooldownUntilTick, "lonelinessCooldownUntilTick", LookMode.Reference, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && lonelinessCooldownUntilTick == null)
            {
                lonelinessCooldownUntilTick = new Dictionary<Pawn, int>();
            }

            Scribe_Collections.Look(ref joinDeniedCooldownUntilTick, "joinDeniedCooldownUntilTick", LookMode.Reference, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && joinDeniedCooldownUntilTick == null)
            {
                joinDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
            }

            Scribe_Collections.Look(ref lonelinessDeniedCooldownUntilTick, "lonelinessDeniedCooldownUntilTick", LookMode.Reference, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && lonelinessDeniedCooldownUntilTick == null)
            {
                lonelinessDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
            }

            Scribe_Collections.Look(ref wanderlustDeniedCooldownUntilTick, "wanderlustDeniedCooldownUntilTick", LookMode.Reference, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && wanderlustDeniedCooldownUntilTick == null)
            {
                wanderlustDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
            }

            Scribe_Collections.Look(ref savedWorkAndSchedule, "savedWorkAndSchedule", LookMode.Reference, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && savedWorkAndSchedule == null)
            {
                savedWorkAndSchedule = new Dictionary<Pawn, PF_WorkAndScheduleSnapshot>();
            }

            Scribe_Collections.Look(ref wanderlustReturnCooldownUntilTick, "wanderlustReturnCooldownUntilTick", LookMode.Reference, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && wanderlustReturnCooldownUntilTick == null)
            {
                wanderlustReturnCooldownUntilTick = new Dictionary<Pawn, int>();
            }

            Scribe_Collections.Look(ref everReturnedFromWanderlust, "everReturnedFromWanderlust", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && everReturnedFromWanderlust == null)
            {
                everReturnedFromWanderlust = new HashSet<Pawn>();
            }
        }

        // Captures the pawn's current work priorities (per WorkTypeDef),
        // timetable (per hour), and apparel policy (Rules/Outfits selection)
        // so they can be restored later. Called right before a colonist
        // leaves the colony (BecomeWanderer/BecomeWanderlustWanderer), while
        // workSettings/timetable/outfits still reflect what the player
        // configured for them.
        public static void SaveWorkAndSchedule(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }

            PF_WorkAndScheduleSnapshot snapshot = new PF_WorkAndScheduleSnapshot();

            if (pawn.workSettings != null)
            {
                foreach (WorkTypeDef workType in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                {
                    snapshot.workPriorities[workType] = pawn.workSettings.GetPriority(workType);
                }
            }

            if (pawn.timetable?.times != null)
            {
                snapshot.timetable = new List<TimeAssignmentDef>(pawn.timetable.times);
            }

            if (pawn.outfits != null)
            {
                snapshot.apparelPolicy = pawn.outfits.CurrentApparelPolicy;
            }

            component.savedWorkAndSchedule[pawn] = snapshot;

            // Independent file-based backup, keyed by ThingID rather than a
            // Scribed Pawn reference - see PF_WorkAndScheduleFileStore for why.
            PF_WorkAndScheduleFileStore.Save(pawn, snapshot);
        }

        // Restores whatever SaveWorkAndSchedule captured for this pawn, if
        // anything - called after ReturnToColony spawns them back in and
        // vanilla has already reset workSettings/timetable to defaults via
        // SetFaction(Faction.OfPlayer). Silently does nothing if there's no
        // snapshot (e.g. the pawn joined the colony some other way).
        public static void RestoreWorkAndSchedule(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }

            bool hasDictSnapshot = component.savedWorkAndSchedule.TryGetValue(pawn, out PF_WorkAndScheduleSnapshot snapshot);
            component.savedWorkAndSchedule.Remove(pawn);

            // The Scribed dictionary is the primary source when it resolved
            // correctly. If it didn't (e.g. the Pawn reference failed to
            // fix up across a save/load while this pawn was off-map), fall
            // back to the file backup instead of silently restoring nothing.
            if (!hasDictSnapshot)
            {
                snapshot = PF_WorkAndScheduleFileStore.TryLoad(pawn);
                if (snapshot == null)
                {
                    return;
                }

                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"RestoreWorkAndSchedule: no Scribed snapshot found for {pawn.LabelShort}, recovered from file backup instead.");
                }
            }

            PF_WorkAndScheduleFileStore.Discard(pawn);

            if (pawn.workSettings != null)
            {
                foreach (KeyValuePair<WorkTypeDef, int> entry in snapshot.workPriorities)
                {
                    if (entry.Key == null || pawn.WorkTypeIsDisabled(entry.Key))
                    {
                        continue;
                    }
                    pawn.workSettings.SetPriority(entry.Key, entry.Value);
                }
            }

            if (pawn.timetable != null && snapshot.timetable != null && snapshot.timetable.Count == pawn.timetable.times.Count)
            {
                for (int i = 0; i < snapshot.timetable.Count; i++)
                {
                    pawn.timetable.SetAssignment(i, snapshot.timetable[i]);
                }
            }

            if (pawn.outfits != null && snapshot.apparelPolicy != null
                && Current.Game.outfitDatabase.AllOutfits.Contains(snapshot.apparelPolicy))
            {
                pawn.outfits.CurrentApparelPolicy = snapshot.apparelPolicy;
            }

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Restored saved work priorities and schedule for returning colonist {pawn.LabelShort}");
            }
        }

        // Pawns currently mid-departure per this mod's own bookkeeping - a
        // saved snapshot exists from SaveWorkAndSchedule and hasn't been
        // consumed by RestoreWorkAndSchedule yet, i.e. they left but haven't
        // come back. Used by PF_WandererTracker's lost-pawn sweep to find
        // pawns who should be tracked but currently aren't (e.g. a departure
        // job that got interrupted between clearing Faction and reaching the
        // toil that calls PF_WandererTracker.Register).
        public static List<Pawn> GetPawnsWithPendingDeparture()
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return new List<Pawn>();
            }

            return component.savedWorkAndSchedule.Keys.ToList();
        }

        // Drops a pending-departure snapshot without restoring it - for a pawn
        // found to have already settled somewhere (e.g. picked up by vanilla's
        // faction "redress" system while untracked) rather than one being
        // recovered back into the tracker. Without this, such a pawn would
        // keep showing up in GetPawnsWithPendingDeparture forever.
        public static void DiscardPendingDeparture(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            component?.savedWorkAndSchedule.Remove(pawn);
            PF_WorkAndScheduleFileStore.Discard(pawn);
        }

        public static void NotifyReturnedFromLoneliness(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }
            component.lonelinessCooldownUntilTick[pawn] = Find.TickManager.TicksGame + LonelinessReturnCooldownTicks;
        }

        // Reuses the same cooldown dictionary as NotifyReturnedFromLoneliness -
        // both represent "just landed in the colony, hasn't had time to build
        // real relationships yet" and should block the loneliness check for
        // the same kind of reason.
        public static void NotifyRecruited(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }
            component.lonelinessCooldownUntilTick[pawn] = Find.TickManager.TicksGame + RecruitedLonelinessCooldownTicks;
        }

        public static void NotifyJoinRequestDenied(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }
            component.joinDeniedCooldownUntilTick[pawn] = Find.TickManager.TicksGame + JoinDeniedCooldownTicks;
        }

        public static void NotifyLonelinessRequestDenied(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }
            component.lonelinessDeniedCooldownUntilTick[pawn] = Find.TickManager.TicksGame + LonelinessDeniedCooldownTicks;
        }

        public static void NotifyWanderlustRequestDenied(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }
            component.wanderlustDeniedCooldownUntilTick[pawn] = Find.TickManager.TicksGame + WanderlustDeniedCooldownTicks;
        }

        // Called once, when a Wanderlust wanderer returns to the colony rather
        // than joining a faction or vanishing. Starts the return cooldown
        // (see WanderlustReturnCooldownTicks) and permanently marks the pawn
        // as a "returning veteran" for text-variant purposes - see
        // IsWanderlustReturnOnCooldown and HasEverReturnedFromWanderlust.
        public static void NotifyReturnedFromWanderlust(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null)
            {
                return;
            }
            component.wanderlustReturnCooldownUntilTick[pawn] = Find.TickManager.TicksGame + WanderlustReturnCooldownTicks;
            component.everReturnedFromWanderlust.Add(pawn);

            // Also shields them from the unrelated Loneliness check for a
            // while - without this a pawn could get the "what a wonderful
            // journey" mood buff and then get flagged as disliked-here almost
            // immediately, since nothing else gates Loneliness eligibility
            // right after a Wanderlust return. Reuses the same dictionary as
            // NotifyReturnedFromLoneliness/NotifyRecruited for the same
            // "hasn't had time to rebuild relationships yet" reasoning.
            component.lonelinessCooldownUntilTick[pawn] = Find.TickManager.TicksGame + LonelinessReturnCooldownTicks;
        }

        // Whether this pawn is still within the post-return cooldown window -
        // used by IsWanderlustEligible to keep them off the candidate list for
        // a while after settling back in.
        public static bool IsWanderlustReturnOnCooldown(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            if (component == null || !component.wanderlustReturnCooldownUntilTick.TryGetValue(pawn, out int untilTick))
            {
                return false;
            }
            return Find.TickManager.TicksGame < untilTick;
        }

        // Whether this pawn has EVER returned from a Wanderlust journey, at
        // any point in this save - permanent, never cleared, and independent
        // of the cooldown above. Used only to pick the "returning veteran"
        // letter text variant when they're offered Wanderlust again.
        public static bool HasEverReturnedFromWanderlust(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            return component != null && component.everReturnedFromWanderlust.Contains(pawn);
        }

        private bool IsOnLonelinessCooldown(Pawn pawn)
        {
            return lonelinessCooldownUntilTick.TryGetValue(pawn, out int untilTick)
                && Find.TickManager.TicksGame < untilTick;
        }

        private bool IsOnJoinDeniedCooldown(Pawn pawn)
        {
            return joinDeniedCooldownUntilTick.TryGetValue(pawn, out int untilTick)
                && Find.TickManager.TicksGame < untilTick;
        }

        private bool IsOnLonelinessDeniedCooldown(Pawn pawn)
        {
            return lonelinessDeniedCooldownUntilTick.TryGetValue(pawn, out int untilTick)
                && Find.TickManager.TicksGame < untilTick;
        }

        private bool IsOnWanderlustDeniedCooldown(Pawn pawn)
        {
            return wanderlustDeniedCooldownUntilTick.TryGetValue(pawn, out int untilTick)
                && Find.TickManager.TicksGame < untilTick;
        }

        public static bool IsLonelinessCooldownActive(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            return component != null && component.IsOnLonelinessCooldown(pawn);
        }

        public static bool IsJoinRequestOnCooldown(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            return component != null && component.IsOnJoinDeniedCooldown(pawn);
        }

        public static bool IsLonelinessRequestOnCooldown(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            return component != null && component.IsOnLonelinessDeniedCooldown(pawn);
        }

        public static bool IsWanderlustRequestOnCooldown(Pawn pawn)
        {
            PF_GameComponent component = Current.Game?.GetComponent<PF_GameComponent>();
            return component != null && component.IsOnWanderlustDeniedCooldown(pawn);
        }

        public override void GameComponentTick()
        {
            int tick = Find.TickManager.TicksGame;

            if ((tick + checkPhaseOffset) % CheckIntervalTicks == 0)
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"GameComponentTick check at tick {tick}, maps={Find.Maps?.Count ?? -1}");
                }

                try
                {
                    DoCheck();
                }
                catch (System.Exception ex)
                {
                    PF_Log.Error("Exception in GameComponentTick: " + ex);
                }
            }

            try
            {
                TickLonelinessScan();
            }
            catch (System.Exception ex)
            {
                PF_Log.Error("Exception in loneliness scan tick: " + ex);
                activeScan = null;
            }
        }

        private void DoCheck()
        {
            if (Find.Maps == null)
            {
                return;
            }

            pendingLonelinessMaps.Clear();

            bool runWanderlustCheck = Find.TickManager.TicksGame >= nextWanderlustCheckTick;
            if (runWanderlustCheck)
            {
                nextWanderlustCheckTick = Find.TickManager.TicksGame + WanderlustCheckIntervalTicks;
            }

            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome)
                {
                    continue;
                }

                CheckVisitorRequest(map);
                if (runWanderlustCheck)
                {
                    CheckWanderlustRequest(map);
                }
                pendingLonelinessMaps.Enqueue(map);
            }
        }

        // Processes at most OpinionEvaluationsPerTick pawn-pair opinions per
        // tick, one map's scan at a time, so a 2500-tick check on a large save
        // turns into many cheap ticks instead of one expensive one.
        private void TickLonelinessScan()
        {
            if (activeScan == null)
            {
                if (pendingLonelinessMaps.Count == 0)
                {
                    return;
                }

                Map map = pendingLonelinessMaps.Dequeue();
                if (map == null || !map.IsPlayerHome)
                {
                    return;
                }

                activeScan = PF_LonelinessScan.Start(map);
                if (activeScan == null)
                {
                    return;
                }
            }

            if (activeScan.Step(OpinionEvaluationsPerTick))
            {
                FinishLonelinessScan(activeScan);
                activeScan = null;
            }
        }

        private void CheckVisitorRequest(Map map)
        {
            if (!Rand.Chance(PeacefulFarewellMod.Settings.requestChancePerVisit))
            {
                return;
            }

            TryForceVisitorRequest(map);
        }

        public static bool TryForceVisitorRequest(Map map)
        {
            if (!FarewellUtility.TryFindRequestCandidate(map, out FarewellUtility.RequestCandidate candidate))
            {
                return false;
            }

            ChoiceLetter_JoinRequest letter = (ChoiceLetter_JoinRequest)LetterMaker.MakeLetter(
                "PF_JoinRequestLabel".Translate(),
                PF_Text.Variant("PF_JoinRequestText", 5, candidate.colonist.Named("PAWN"), candidate.relatedPawn.Named("RELATED"), candidate.targetFaction.Name),
                PF_LetterDefOf.PF_JoinRequest);
            letter.colonist = candidate.colonist;
            letter.relatedPawn = candidate.relatedPawn;
            letter.visitingLord = candidate.visitingLord;
            letter.targetFaction = candidate.targetFaction;
            letter.lookTargets = new LookTargets(candidate.colonist);

            Find.LetterStack.ReceiveLetter(letter);

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Letter sent for colonist {candidate.colonist.LabelShort} -> {candidate.targetFaction.Name}");
            }

            return true;
        }

        private void FinishLonelinessScan(PF_LonelinessScan scan)
        {
            Map map = scan.Map;
            if (map == null || !map.IsPlayerHome)
            {
                return;
            }

            List<Pawn> lonelyNow = scan.Results;

            // Anyone who currently carries the "not liked here" thought but no
            // longer qualifies (opinions improved) simply loses the thought -
            // no letter, nothing else happens.
            foreach (Pawn colonist in map.mapPawns.FreeColonistsSpawned)
            {
                if (!lonelyNow.Contains(colonist) && FarewellUtility.HasNotLikedHereThought(colonist))
                {
                    FarewellUtility.RemoveNotLikedHereThought(colonist);
                }
            }

            if (lonelyNow.Count == 0)
            {
                return;
            }

            // Colonists who just started qualifying get the thought this check,
            // but don't get a letter yet - that only happens once they've
            // already been carrying the thought for at least one prior check.
            List<Pawn> readyForLetter = new List<Pawn>();
            foreach (Pawn pawn in lonelyNow)
            {
                // Freshly recruited or freshly returned from wandering off - they
                // haven't had time to build real opinions with the rest of the
                // colony yet, so skip them entirely (no thought, no letter) until
                // the cooldown lapses, instead of flagging them as disliked for
                // simply lacking history.
                if (IsOnLonelinessCooldown(pawn))
                {
                    continue;
                }

                if (FarewellUtility.HasNotLikedHereThought(pawn))
                {
                    // Still stewing over a recent rejection - give the player the
                    // time that cooldown is meant for before asking again.
                    if (!IsOnLonelinessDeniedCooldown(pawn))
                    {
                        readyForLetter.Add(pawn);
                    }
                }
                else
                {
                    FarewellUtility.GiveNotLikedHereThought(pawn);
                }
            }

            if (readyForLetter.Count == 0)
            {
                return;
            }

            if (!Rand.Chance(PeacefulFarewellMod.Settings.lonelinessChancePerCheck))
            {
                return;
            }

            Pawn candidate = readyForLetter.RandomElement();
            SendLonelinessLetter(candidate);
        }

        public static bool TryForceLonelinessRequest(Map map)
        {
            List<Pawn> candidates = map.mapPawns.FreeColonistsSpawned
                .Where((Pawn p) => FarewellUtility.IsEligibleColonist(p) && p.DevelopmentalStage.Adult())
                .ToList();

            if (candidates.Count == 0)
            {
                return false;
            }

            SendLonelinessLetter(candidates.RandomElement());
            return true;
        }

        private static void SendLonelinessLetter(Pawn candidate)
        {
            ChoiceLetter_LonelinessRequest letter = (ChoiceLetter_LonelinessRequest)LetterMaker.MakeLetter(
                "PF_LonelinessRequestLabel".Translate(),
                PF_Text.Variant("PF_LonelinessRequestText", 5, candidate.Named("PAWN")),
                PF_LetterDefOf.PF_LonelinessRequest);
            letter.colonist = candidate;
            letter.lookTargets = new LookTargets(candidate);

            Find.LetterStack.ReceiveLetter(letter);

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Loneliness letter sent for colonist {candidate.LabelShort}");
            }
        }

        private void CheckWanderlustRequest(Map map)
        {
            if (!PeacefulFarewellMod.Settings.wanderlustEnabled)
            {
                return;
            }

            List<Pawn> candidates = FarewellUtility.FindWanderlustCandidates(map);
            if (candidates.Count == 0)
            {
                return;
            }

            float chance = PeacefulFarewellMod.Settings.wanderlustChancePerCheck;
            foreach (Pawn pawn in candidates)
            {
                if (!Rand.Chance(chance))
                {
                    continue;
                }

                SendWanderlustLetter(pawn);

                // Only one wanderlust request per check - if several teenagers rolled
                // in the same tick, the rest simply get another chance next check.
                return;
            }
        }

        public static bool TryForceWanderlustRequest(Map map)
        {
            List<Pawn> candidates = FarewellUtility.FindWanderlustCandidates(map);
            if (candidates.Count == 0)
            {
                return false;
            }

            SendWanderlustLetter(candidates.RandomElement());
            return true;
        }

        private static void SendWanderlustLetter(Pawn pawn)
        {
            string textKey = HasEverReturnedFromWanderlust(pawn) ? "PF_WanderlustRequestReturningText" : "PF_WanderlustRequestText";
            ChoiceLetter_WanderlustRequest letter = (ChoiceLetter_WanderlustRequest)LetterMaker.MakeLetter(
                "PF_WanderlustRequestLabel".Translate(),
                PF_Text.Variant(textKey, 5, pawn.Named("PAWN")),
                PF_LetterDefOf.PF_WanderlustRequest);
            letter.colonist = pawn;
            letter.lookTargets = new LookTargets(pawn);

            Find.LetterStack.ReceiveLetter(letter);

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Wanderlust letter sent for colonist {pawn.LabelShort}");
            }
        }
    }
}
