using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace PeacefulFarewell
{
    public class PF_GameComponent : GameComponent
    {
        private const int CheckIntervalTicks = 2500;

        // Rolled once per in-game day rather than hourly: the 13-18 age band
        // spans 5 years, and hourly rolls would make even a 0.5% chance fire
        // for almost every teenager within days of turning 13.
        private const int WanderlustCheckIntervalTicks = GenDate.TicksPerDay;
        private int nextWanderlustCheckTick = -1;

        // The loneliness scan is O(n^2) OpinionOf calls - spread across ticks
        // with a time budget per tick (not a fixed pair count: OpinionOf gets
        // slower the more thoughts pawns carry, so 200 pairs per tick cost a
        // few ms on a ~280-colonist save and showed up as FPS drops, worse
        // at higher game speeds where several ticks run per frame).
        private const double LonelinessScanBudgetMsPerTick = 0.25;
        // One OpinionOf can cost ~0.3 ms on its own on a big modded save, so
        // the guaranteed minimum must stay at a single evaluation - a floor
        // of 10 used to push every scan tick to ~5 ms regardless of budget.
        private const int LonelinessScanMinEvaluationsPerTick = 1;

        // Pawn -> colonist whose opinion disqualified them from loneliness on
        // the last scan; asked first next time (see PF_LonelinessScan). A
        // pure speed hint - not saved, rebuilt by the first scan after load.
        private readonly Dictionary<Pawn, Pawn> lonelinessLastFan = new Dictionary<Pawn, Pawn>();
        private static readonly long LonelinessScanBudgetStopwatchTicks =
            (long)(Stopwatch.Frequency * LonelinessScanBudgetMsPerTick / 1000.0);

        // Visitor-candidate scans are queued and run one map per tick instead
        // of every home map in the same tick.
        private readonly Queue<Map> pendingVisitorMaps = new Queue<Map>();

        // Debug-mode perf log: a stage is only reported when it took longer
        // than this, so the log stays readable.
        private const double PerfLogThresholdMs = 1.0;

        // "Just landed in the colony" shields from the loneliness check -
        // after returning from wandering off, or after being recruited - so
        // relationships have a chance to form before anyone is judged on them.
        private const int LonelinessReturnCooldownTicks = 5 * GenDate.TicksPerDay;
        private const int RecruitedLonelinessCooldownTicks = 5 * GenDate.TicksPerDay;

        // After the player refuses a request, it isn't offered again for as
        // long as the matching "denied" thought lasts (ThoughtDefs XML).
        private const int JoinDeniedCooldownTicks = 4 * GenDate.TicksPerDay;
        private const int LonelinessDeniedCooldownTicks = 4 * GenDate.TicksPerDay;
        private const int WanderlustDeniedCooldownTicks = 3 * GenDate.TicksPerDay;

        // A pawn back from a Wanderlust journey isn't offered another one for
        // 2 in-game years (not permanent - they may miss the road again).
        private const int WanderlustReturnCooldownTicks = 2 * GenDate.DaysPerYear * GenDate.TicksPerDay;

        private Queue<Map> pendingLonelinessMaps = new Queue<Map>();
        private PF_LonelinessScan activeScan;
        private Dictionary<Pawn, int> lonelinessCooldownUntilTick = new Dictionary<Pawn, int>();
        private Dictionary<Pawn, int> joinDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
        private Dictionary<Pawn, int> lonelinessDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
        private Dictionary<Pawn, int> wanderlustDeniedCooldownUntilTick = new Dictionary<Pawn, int>();
        private Dictionary<Pawn, int> wanderlustReturnCooldownUntilTick = new Dictionary<Pawn, int>();

        // Pawns who have EVER returned from a Wanderlust journey - never
        // cleared, only picks the "returning veteran" letter text.
        private HashSet<Pawn> everReturnedFromWanderlust = new HashSet<Pawn>();

        // Work priorities, schedule and apparel policy of a colonist who left,
        // restored if they come back (SetFaction(OfPlayer) resets them).
        private Dictionary<Pawn, PF_WorkAndScheduleSnapshot> savedWorkAndSchedule = new Dictionary<Pawn, PF_WorkAndScheduleSnapshot>();

        // Scribe working lists - dictionaries keyed by a Pawn reference need
        // them to survive the load passes; without them the dictionaries came
        // back empty after every load.
        private readonly List<Pawn>[] tmpKeys = new List<Pawn>[7];
        private readonly List<int>[] tmpTicks = new List<int>[6];
        private List<PF_WorkAndScheduleSnapshot> tmpSnapshots;

        // Random per-save phase for the hourly check, so it doesn't always
        // fire on the very first tick after loading at a day boundary.
        private int checkPhaseOffset = -1;

        // Colonists meeting a close relation/friend among friendly visitors
        // (see PF_VisitMeeting), ticked every VisitMeetingTickInterval.
        private List<PF_VisitMeeting> visitMeetings = new List<PF_VisitMeeting>();
        private const int VisitMeetingTickInterval = 250;

        // First talk within an hour of the meeting starting, then one every
        // 3-4 in-game hours - a one-day caravan visit fits the default 3.
        private static readonly IntRange FirstVisitTalkDelayTicks = new IntRange(250, GenDate.TicksPerHour);
        private static readonly IntRange VisitTalkIntervalTicks = new IntRange(3 * GenDate.TicksPerHour, 4 * GenDate.TicksPerHour);

        // If a started talk falls through, wait this long before trying again.
        private const int VisitTalkRetryTicks = GenDate.TicksPerHour;

        // Passersby are on the map for a few hours at most - the colonist
        // goes right away and retries quickly.
        private static readonly IntRange PasserbyFirstTalkDelayTicks = new IntRange(0, VisitMeetingTickInterval);
        private const int PasserbyTalkRetryTicks = 1000;

        // A talk that ended with nothing said counts as a failed attempt;
        // after this many in a row the colonist gives up for this visit
        // instead of running after the visitor over and over.
        private const int MaxFailedPasserbyTalkAttempts = 2;
        private const int MaxFailedVisitTalkAttempts = 4;

        // Not every passing group gets noticed - rolled once per group and
        // colonist (an unnoticed one gets a concluded meeting right away, so
        // the hourly scan doesn't roll again for the same group).
        private const float PasserbyNoticeChance = 0.5f;

        // After meeting (or ignoring) one passerby, a colonist doesn't chase
        // the next traveler group for a while - on busy maps groups with
        // someone they know can come by several times a day.
        private const int PasserbyMeetingCooldownTicks = 2 * GenDate.TicksPerDay;
        private Dictionary<Pawn, int> passerbyCooldownUntilTick = new Dictionary<Pawn, int>();

        private static int MaxFailedTalkAttempts(PF_VisitMeeting meeting)
        {
            return meeting.passerby ? MaxFailedPasserbyTalkAttempts : MaxFailedVisitTalkAttempts;
        }

        private static int TalkRetryTicks(PF_VisitMeeting meeting)
        {
            return meeting.passerby ? PasserbyTalkRetryTicks : VisitTalkRetryTicks;
        }

        private static int TalksNeeded(PF_VisitMeeting meeting)
        {
            return meeting.passerby ? 1 : PeacefulFarewellMod.Settings.visitTalksBeforeDecision;
        }

        public PF_GameComponent(Game game)
        {
        }

        private static PF_GameComponent Instance => Current.Game?.GetComponent<PF_GameComponent>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref checkPhaseOffset, "checkPhaseOffset", -1);
            Scribe_Values.Look(ref nextWanderlustCheckTick, "nextWanderlustCheckTick", -1);

            LookPawnDict(ref lonelinessCooldownUntilTick, "lonelinessCooldownUntilTick", LookMode.Value, ref tmpKeys[0], ref tmpTicks[0]);
            LookPawnDict(ref joinDeniedCooldownUntilTick, "joinDeniedCooldownUntilTick", LookMode.Value, ref tmpKeys[1], ref tmpTicks[1]);
            LookPawnDict(ref lonelinessDeniedCooldownUntilTick, "lonelinessDeniedCooldownUntilTick", LookMode.Value, ref tmpKeys[2], ref tmpTicks[2]);
            LookPawnDict(ref wanderlustDeniedCooldownUntilTick, "wanderlustDeniedCooldownUntilTick", LookMode.Value, ref tmpKeys[3], ref tmpTicks[3]);
            LookPawnDict(ref savedWorkAndSchedule, "savedWorkAndSchedule", LookMode.Deep, ref tmpKeys[4], ref tmpSnapshots);
            LookPawnDict(ref wanderlustReturnCooldownUntilTick, "wanderlustReturnCooldownUntilTick", LookMode.Value, ref tmpKeys[5], ref tmpTicks[4]);
            LookPawnDict(ref passerbyCooldownUntilTick, "passerbyCooldownUntilTick", LookMode.Value, ref tmpKeys[6], ref tmpTicks[5]);

            Scribe_Collections.Look(ref everReturnedFromWanderlust, "everReturnedFromWanderlust", LookMode.Reference);
            Scribe_Collections.Look(ref visitMeetings, "visitMeetings", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (checkPhaseOffset < 0)
                {
                    checkPhaseOffset = Rand.Range(0, CheckIntervalTicks);
                }
                if (nextWanderlustCheckTick < 0)
                {
                    nextWanderlustCheckTick = Find.TickManager.TicksGame + WanderlustCheckIntervalTicks;
                }
                everReturnedFromWanderlust = everReturnedFromWanderlust ?? new HashSet<Pawn>();
                visitMeetings = visitMeetings ?? new List<PF_VisitMeeting>();
                visitMeetings.RemoveAll((PF_VisitMeeting m) => m == null || m.colonist == null || m.relative == null);
            }
        }

        private static void LookPawnDict<V>(ref Dictionary<Pawn, V> dict, string label, LookMode valueLookMode, ref List<Pawn> keys, ref List<V> values)
        {
            // logNullErrors off: a pawn that no longer exists (e.g. discarded
            // after dying) just drops out of the dictionary.
            Scribe_Collections.Look(ref dict, label, LookMode.Reference, valueLookMode, ref keys, ref values, logNullErrors: false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && dict == null)
            {
                dict = new Dictionary<Pawn, V>();
            }
        }

        // Captures the pawn's work priorities, timetable and apparel policy
        // right before they leave (BecomeWanderer/BecomeWanderlustWanderer).
        public static void SaveWorkAndSchedule(Pawn pawn)
        {
            PF_GameComponent component = Instance;
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

            // Independent file-based backup, keyed by ThingID - see
            // PF_WorkAndScheduleFileStore.
            PF_WorkAndScheduleFileStore.Save(pawn, snapshot);
        }

        // Restores what SaveWorkAndSchedule captured, after ReturnToColony
        // spawned the pawn back in. Does nothing if there's no snapshot.
        public static void RestoreWorkAndSchedule(Pawn pawn)
        {
            PF_GameComponent component = Instance;
            if (component == null)
            {
                return;
            }

            bool hasDictSnapshot = component.savedWorkAndSchedule.TryGetValue(pawn, out PF_WorkAndScheduleSnapshot snapshot);
            component.savedWorkAndSchedule.Remove(pawn);

            // Fall back to the file backup if the scribed entry didn't survive.
            if (!hasDictSnapshot)
            {
                snapshot = PF_WorkAndScheduleFileStore.TryLoad(pawn);
                if (snapshot == null)
                {
                    return;
                }

                if (PF_Log.DebugMode)
                    PF_Log.Message($"RestoreWorkAndSchedule: no Scribed snapshot found for {pawn.LabelShort}, recovered from file backup instead.");
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

            if (PF_Log.DebugMode)
                PF_Log.Message($"Restored saved work priorities and schedule for returning colonist {pawn.LabelShort}");
        }

        // Pawns who left (snapshot saved) but haven't come back - used by
        // PF_WandererTracker's lost-pawn sweep to find untracked departures.
        public static List<Pawn> GetPawnsWithPendingDeparture()
        {
            PF_GameComponent component = Instance;
            return component != null ? component.savedWorkAndSchedule.Keys.ToList() : new List<Pawn>();
        }

        // Drops a pending-departure snapshot without restoring it (the pawn
        // already settled somewhere), so it stops showing up above.
        public static void DiscardPendingDeparture(Pawn pawn)
        {
            Instance?.savedWorkAndSchedule.Remove(pawn);
            PF_WorkAndScheduleFileStore.Discard(pawn);
        }

        // ---- Cooldowns ----

        private static void SetCooldown(Dictionary<Pawn, int> cooldowns, Pawn pawn, int ticks)
        {
            if (cooldowns != null)
            {
                cooldowns[pawn] = Find.TickManager.TicksGame + ticks;
            }
        }

        private static bool OnCooldown(Dictionary<Pawn, int> cooldowns, Pawn pawn)
        {
            return cooldowns != null && cooldowns.TryGetValue(pawn, out int untilTick) && Find.TickManager.TicksGame < untilTick;
        }

        public static void NotifyReturnedFromLoneliness(Pawn pawn) => SetCooldown(Instance?.lonelinessCooldownUntilTick, pawn, LonelinessReturnCooldownTicks);
        public static void NotifyRecruited(Pawn pawn) => SetCooldown(Instance?.lonelinessCooldownUntilTick, pawn, RecruitedLonelinessCooldownTicks);
        public static void NotifyJoinRequestDenied(Pawn pawn) => SetCooldown(Instance?.joinDeniedCooldownUntilTick, pawn, JoinDeniedCooldownTicks);
        public static void NotifyLonelinessRequestDenied(Pawn pawn) => SetCooldown(Instance?.lonelinessDeniedCooldownUntilTick, pawn, LonelinessDeniedCooldownTicks);
        public static void NotifyWanderlustRequestDenied(Pawn pawn) => SetCooldown(Instance?.wanderlustDeniedCooldownUntilTick, pawn, WanderlustDeniedCooldownTicks);

        // Starts the Wanderlust return cooldown, marks the pawn as a
        // "returning veteran", and also shields them from the loneliness
        // check for a while, like any other fresh arrival.
        public static void NotifyReturnedFromWanderlust(Pawn pawn)
        {
            PF_GameComponent component = Instance;
            if (component == null)
            {
                return;
            }
            SetCooldown(component.wanderlustReturnCooldownUntilTick, pawn, WanderlustReturnCooldownTicks);
            component.everReturnedFromWanderlust.Add(pawn);
            SetCooldown(component.lonelinessCooldownUntilTick, pawn, LonelinessReturnCooldownTicks);
        }

        public static bool IsWanderlustReturnOnCooldown(Pawn pawn) => OnCooldown(Instance?.wanderlustReturnCooldownUntilTick, pawn);
        public static bool HasEverReturnedFromWanderlust(Pawn pawn) => Instance?.everReturnedFromWanderlust.Contains(pawn) == true;
        public static bool IsLonelinessCooldownActive(Pawn pawn) => OnCooldown(Instance?.lonelinessCooldownUntilTick, pawn);
        public static bool IsJoinRequestOnCooldown(Pawn pawn) => OnCooldown(Instance?.joinDeniedCooldownUntilTick, pawn);
        public static bool IsLonelinessRequestOnCooldown(Pawn pawn) => OnCooldown(Instance?.lonelinessDeniedCooldownUntilTick, pawn);
        public static bool IsWanderlustRequestOnCooldown(Pawn pawn) => OnCooldown(Instance?.wanderlustDeniedCooldownUntilTick, pawn);

        public override void GameComponentTick()
        {
            int tick = Find.TickManager.TicksGame;

            if ((tick + checkPhaseOffset) % CheckIntervalTicks == 0)
            {
                if (PF_Log.DebugMode)
                    PF_Log.Message($"GameComponentTick check at tick {tick}, maps={Find.Maps?.Count ?? -1}");

                try
                {
                    DoCheck();
                }
                catch (System.Exception ex)
                {
                    PF_Log.Error("Exception in GameComponentTick: " + ex);
                }
            }

            if (pendingVisitorMaps.Count > 0)
            {
                Map map = pendingVisitorMaps.Dequeue();
                try
                {
                    if (map != null && map.IsPlayerHome && Find.Maps.Contains(map))
                    {
                        long start = Stopwatch.GetTimestamp();
                        CheckVisitorRequest(map);
                        LogPerf("Visitor candidate scan on " + map, start);
                    }
                }
                catch (System.Exception ex)
                {
                    PF_Log.Error("Exception in visitor request check: " + ex);
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

            if (tick % VisitMeetingTickInterval == 0)
            {
                try
                {
                    TickVisitMeetings();
                }
                catch (System.Exception ex)
                {
                    PF_Log.Error("Exception in visit meeting tick: " + ex);
                }
            }
        }

        private void DoCheck()
        {
            if (Find.Maps == null)
            {
                return;
            }

            // Loneliness maps are NOT cleared and re-queued: if a scan cycle
            // ever runs longer than CheckIntervalTicks, clearing would restart
            // from the first map every time and starve the maps at the end.
            pendingVisitorMaps.Clear();
            PruneLonelinessLastFan();

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

                pendingVisitorMaps.Enqueue(map);
                if (activeScan?.Map != map && !pendingLonelinessMaps.Contains(map))
                {
                    pendingLonelinessMaps.Enqueue(map);
                }
            }

            if (runWanderlustCheck)
            {
                long start = Stopwatch.GetTimestamp();
                CheckWanderlustRequest();
                LogPerf("Wanderlust check", start);
            }
        }

        // Drops hint entries for pawns that are gone, so the dictionary
        // doesn't keep dead/discarded pawns alive over a long game.
        private readonly List<Pawn> tmpFanKeysToRemove = new List<Pawn>();

        private void PruneLonelinessLastFan()
        {
            foreach (KeyValuePair<Pawn, Pawn> entry in lonelinessLastFan)
            {
                if (entry.Key == null || entry.Key.Dead || entry.Key.Discarded
                    || entry.Value == null || entry.Value.Dead || entry.Value.Discarded)
                {
                    tmpFanKeysToRemove.Add(entry.Key);
                }
            }
            foreach (Pawn key in tmpFanKeysToRemove)
            {
                lonelinessLastFan.Remove(key);
            }
            tmpFanKeysToRemove.Clear();
        }

        // Debug mode only: reports a stage that took longer than
        // PerfLogThresholdMs, to find what causes FPS drops on big saves.
        internal static void LogPerf(string stage, long startTimestamp)
        {
            if (!PF_Log.DebugMode)
            {
                return;
            }
            double ms = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            if (ms >= PerfLogThresholdMs)
            {
                PF_Log.Message($"[perf] {stage}: {ms:F1} ms");
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
                if (map == null || !map.IsPlayerHome || !Find.Maps.Contains(map))
                {
                    return;
                }

                activeScan = PF_LonelinessScan.Start(map, lonelinessLastFan);
                if (activeScan == null)
                {
                    return;
                }
            }

            long now = Stopwatch.GetTimestamp();
            if (activeScan.Step(now + LonelinessScanBudgetStopwatchTicks, LonelinessScanMinEvaluationsPerTick))
            {
                FinishLonelinessScan(activeScan);
                activeScan = null;
            }
        }

        // No dice roll any more: every colonist with a close relation/friend
        // among the visitors gets a meeting, and the talks decide.
        private void CheckVisitorRequest(Map map, bool debugForce = false)
        {
            // Colonists already in a meeting are skipped before the expensive
            // relation/friendship checks rather than after - otherwise every
            // hourly check re-ran them for the whole visit.
            foreach (FarewellUtility.RequestCandidate candidate in FarewellUtility.FindRequestCandidates(map, HasVisitMeeting, forTalks: true))
            {
                // A colonist can qualify for several visiting groups at once -
                // the first one wins. Also skips colonists whose meeting
                // already ended in a decision during this visit.
                if (HasVisitMeeting(candidate.colonist))
                {
                    continue;
                }
                if (candidate.passerby && !debugForce && OnCooldown(passerbyCooldownUntilTick, candidate.colonist))
                {
                    continue;
                }

                PF_VisitMeeting meeting = new PF_VisitMeeting
                {
                    colonist = candidate.colonist,
                    relative = candidate.relatedPawn,
                    lord = candidate.visitingLord,
                    faction = candidate.targetFaction,
                    passerby = candidate.passerby,
                    nextTalkTick = Find.TickManager.TicksGame + (candidate.passerby ? PasserbyFirstTalkDelayTicks : FirstVisitTalkDelayTicks).RandomInRange
                };
                if (candidate.passerby)
                {
                    SetCooldown(passerbyCooldownUntilTick, candidate.colonist, PasserbyMeetingCooldownTicks);
                    // Didn't notice them: kept as a concluded meeting until
                    // the group is gone, so no second roll for it.
                    if (!debugForce && !Rand.Chance(PasserbyNoticeChance))
                    {
                        meeting.concluded = true;
                    }
                }
                visitMeetings.Add(meeting);

                if (PF_Log.DebugMode)
                    PF_Log.Message(meeting.concluded ? $"Passerby not noticed: {meeting}" : $"Visit meeting started: {meeting}");
            }
        }

        private bool HasVisitMeeting(Pawn colonist)
        {
            foreach (PF_VisitMeeting meeting in visitMeetings)
            {
                if (meeting.colonist == colonist)
                {
                    return true;
                }
            }
            return false;
        }

        private void TickVisitMeetings()
        {
            if (visitMeetings.Count == 0)
            {
                return;
            }

            int now = Find.TickManager.TicksGame;
            for (int i = visitMeetings.Count - 1; i >= 0; i--)
            {
                PF_VisitMeeting meeting = visitMeetings[i];

                if (meeting.passerby && meeting.concluded && TickPendingPasserbyLetter(meeting, out bool remove))
                {
                    if (remove)
                    {
                        visitMeetings.RemoveAt(i);
                    }
                    continue;
                }

                if (!FarewellUtility.IsVisitMeetingStillPossible(meeting))
                {
                    visitMeetings.RemoveAt(i);
                    if (!meeting.concluded)
                    {
                        PartWithoutDecision(meeting);
                    }
                    continue;
                }

                if (meeting.concluded)
                {
                    continue;
                }
                // While a talk (or the run over to it) is going on, keep
                // pushing the retry back - so a failed attempt waits the full
                // retry delay from when it ended, not from when it started
                // (the approach alone can take longer than the delay).
                if (meeting.colonist.CurJobDef == PF_JobDefOf.PF_TalkWithVisitingRelative)
                {
                    meeting.nextTalkTick = System.Math.Max(meeting.nextTalkTick, now + TalkRetryTicks(meeting));
                    continue;
                }
                if (now < meeting.nextTalkTick)
                {
                    continue;
                }
                // The previous attempt ended without a talk that counted
                // (NotifyVisitTalkFinished clears the flag when one does).
                if (meeting.talkAttemptPending)
                {
                    meeting.talkAttemptPending = false;
                    meeting.failedAttempts++;
                    if (meeting.failedAttempts >= MaxFailedTalkAttempts(meeting))
                    {
                        meeting.concluded = true;
                        PartWithoutDecision(meeting);
                        continue;
                    }
                }
                if (!FarewellUtility.CanStartVisitTalk(meeting.colonist, meeting.relative, meeting.debugForceNextTalk, out string blockReason))
                {
                    if (PF_Log.DebugMode && blockReason != meeting.lastBlockReason)
                        PF_Log.Message($"Visit talk blocked ({blockReason}): {meeting}");
                    meeting.lastBlockReason = blockReason;
                    continue;
                }

                meeting.lastBlockReason = null;
                meeting.debugForceNextTalk = false;
                meeting.nextTalkTick = now + TalkRetryTicks(meeting);
                FarewellUtility.StartVisitTalk(meeting.colonist, meeting.relative, meeting.passerby);
                meeting.talkAttemptPending = meeting.colonist.CurJobDef == PF_JobDefOf.PF_TalkWithVisitingRelative;

                if (PF_Log.DebugMode)
                    PF_Log.Message($"Visit talk started: {meeting}, colonist job now={meeting.colonist.CurJobDef?.defName ?? "null"}");
            }
        }

        // A passerby's join letter is still unanswered: the meeting stays
        // until the traveler is gone (not just "leaving" - they're still
        // walking to the edge and can be caught). Once they're off the map
        // the letter expires and the colonist regrets not saying goodbye.
        // Returns false when there's no such letter (normal handling).
        private static bool TickPendingPasserbyLetter(PF_VisitMeeting meeting, out bool remove)
        {
            remove = false;
            ChoiceLetter_JoinRequest letter = FarewellUtility.FindPendingPasserbyLetter(meeting.colonist, meeting.relative);
            if (letter == null)
            {
                return false;
            }

            Pawn colonist = meeting.colonist;
            Pawn relative = meeting.relative;
            bool colonistGone = colonist == null || colonist.Dead || colonist.Faction != Faction.OfPlayer;
            bool relativeGone = relative == null || relative.Dead || !relative.Spawned || colonist == null || relative.MapHeld != colonist.MapHeld;
            if (!colonistGone && !relativeGone)
            {
                return true;
            }

            remove = true;
            letter.decided = true;
            Find.LetterStack.RemoveLetter(letter);
            if (!colonistGone)
            {
                FarewellUtility.GiveMissedGoodbyeThought(colonist, relative);
                Messages.Message("PF_PasserbyLeftUnanswered".Translate(colonist.Named("PAWN"), relative.Named("RELATIVE")),
                    colonist, MessageTypeDefOf.NegativeEvent, historical: false);
            }

            if (PF_Log.DebugMode)
                PF_Log.Message($"Passerby left with the join letter unanswered: {meeting}");
            return true;
        }

        // The visitors left (or either pawn became unavailable) before the
        // last talk - no decision, the colonist just stays.
        private static void PartWithoutDecision(PF_VisitMeeting meeting)
        {
            if (meeting.talksDone > 0 && meeting.snubs == 0 && meeting.colonist != null
                && !meeting.colonist.Dead && meeting.colonist.Faction == Faction.OfPlayer)
            {
                GiveGoodToSeeThought(meeting);
            }

            if (PF_Log.DebugMode)
                PF_Log.Message($"Visit meeting ended without a decision: {meeting}");
        }

        // Called by JobDriver_TalkWithVisitingRelative when a talk ends.
        public static void NotifyVisitTalkFinished(Pawn colonist, Pawn relative, FarewellUtility.VisitTalkOutcome outcome)
        {
            PF_GameComponent component = Instance;
            if (component == null)
            {
                return;
            }

            PF_VisitMeeting meeting = component.visitMeetings.FirstOrDefault((PF_VisitMeeting m) => m.colonist == colonist && m.relative == relative && !m.concluded);
            if (meeting == null || outcome == FarewellUtility.VisitTalkOutcome.None)
            {
                return;
            }

            meeting.talkAttemptPending = false;
            meeting.failedAttempts = 0;
            meeting.talksDone++;
            meeting.talkPoints += FarewellUtility.VisitTalkPoints(outcome);
            if (outcome == FarewellUtility.VisitTalkOutcome.Snub)
            {
                meeting.snubs++;
            }
            meeting.nextTalkTick = Find.TickManager.TicksGame + VisitTalkIntervalTicks.RandomInRange;

            if (PF_Log.DebugMode)
                PF_Log.Message($"Visit talk finished ({outcome}): {meeting}");

            if (meeting.talksDone >= TalksNeeded(meeting))
            {
                DecideVisitMeeting(meeting);
            }
        }

        private static void DecideVisitMeeting(PF_VisitMeeting meeting)
        {
            meeting.concluded = true;

            float pull = FarewellUtility.ComputeVisitPull(meeting, out string breakdown);
            bool wantsToLeave = pull >= FarewellUtility.VisitPullThreshold;
            // Family or close friends in the colony keep the colonist here no
            // matter how the talks went - they just enjoyed the visit.
            bool joinAllowed = FarewellUtility.CanBeOfferedJoin(meeting.colonist, meeting.relative, out string joinBlock);
            bool tiedToColony = !joinAllowed && !FarewellUtility.HasPendingRequestLetter(meeting.colonist)
                && !IsJoinRequestOnCooldown(meeting.colonist);
            bool canAsk = wantsToLeave
                && joinAllowed
                && meeting.faction != null
                && !meeting.faction.HostileTo(Faction.OfPlayer);

            if (PF_Log.DebugMode)
                PF_Log.Message($"Visit meeting decision for {meeting.colonist.LabelShort}: {breakdown} -> {(canAsk ? "asks to leave" : wantsToLeave ? $"wants to leave, but can't ask ({joinBlock ?? "faction"})" : "stays")}");

            if (canAsk)
            {
                SendJoinRequestLetter(meeting.colonist, meeting.relative, meeting.lord, meeting.faction, afterTalks: true, meeting.passerby);
                return;
            }

            if ((!wantsToLeave || tiedToColony) && meeting.snubs == 0)
            {
                GiveGoodToSeeThought(meeting);
            }
        }

        private static void GiveGoodToSeeThought(PF_VisitMeeting meeting)
        {
            if (meeting.passerby)
            {
                FarewellUtility.GiveGoodToSeePasserbyThought(meeting.colonist, meeting.relative);
            }
            else
            {
                FarewellUtility.GiveGoodToSeeVisitorThought(meeting.colonist, meeting.relative);
            }
        }

        // Debug: start meetings on this map right away and make every
        // undecided meeting there due for its next talk now, skipping the
        // soft checks (work, schedule, needs) in CanStartVisitTalk.
        public static int DebugHurryVisitTalks(Map map)
        {
            PF_GameComponent component = Instance;
            if (component == null || map == null)
            {
                return 0;
            }

            component.CheckVisitorRequest(map, debugForce: true);
            int count = 0;
            foreach (PF_VisitMeeting meeting in component.visitMeetings)
            {
                if (!meeting.concluded && meeting.colonist?.Map == map)
                {
                    meeting.nextTalkTick = Find.TickManager.TicksGame;
                    meeting.debugForceNextTalk = true;
                    meeting.lastBlockReason = null;
                    count++;
                }
            }
            return count;
        }

        public static List<PF_VisitMeeting> GetVisitMeetings()
        {
            return Instance?.visitMeetings ?? new List<PF_VisitMeeting>();
        }

        // Debug-only: skips the talks and sends the letter straight away.
        public static bool TryForceVisitorRequest(Map map)
        {
            if (!FarewellUtility.TryFindRequestCandidate(map, out FarewellUtility.RequestCandidate candidate))
            {
                return false;
            }

            SendJoinRequestLetter(candidate.colonist, candidate.relatedPawn, candidate.visitingLord, candidate.targetFaction, afterTalks: false, candidate.passerby);
            return true;
        }

        private static void SendJoinRequestLetter(Pawn colonist, Pawn relatedPawn, Lord visitingLord, Faction targetFaction, bool afterTalks, bool passerby = false)
        {
            FarewellUtility.JoinBond bond = FarewellUtility.GetJoinBond(colonist, relatedPawn, out PawnRelationDef otherFamilyRelation);
            ChoiceLetter_JoinRequest letter = (ChoiceLetter_JoinRequest)LetterMaker.MakeLetter(
                FarewellUtility.MakeJoinRequestLabel(bond),
                FarewellUtility.MakeJoinRequestText(colonist, relatedPawn, targetFaction, bond, otherFamilyRelation, afterTalks),
                PF_LetterDefOf.PF_JoinRequest);
            letter.colonist = colonist;
            letter.relatedPawn = relatedPawn;
            letter.visitingLord = visitingLord;
            letter.targetFaction = targetFaction;
            letter.passerby = passerby;
            letter.lookTargets = new LookTargets(colonist);

            Find.LetterStack.ReceiveLetter(letter);

            if (PF_Log.DebugMode)
                PF_Log.Message($"Letter sent for colonist {colonist.LabelShort} -> {targetFaction.Name} (bond={bond}, relation={otherFamilyRelation?.defName ?? "-"}, afterTalks={afterTalks})");
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
                if (OnCooldown(lonelinessCooldownUntilTick, pawn))
                {
                    continue;
                }

                if (FarewellUtility.HasNotLikedHereThought(pawn))
                {
                    // Still stewing over a recent rejection - give the player the
                    // time that cooldown is meant for before asking again.
                    if (!OnCooldown(lonelinessDeniedCooldownUntilTick, pawn) && !FarewellUtility.HasPendingRequestLetter(pawn))
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

            if (PF_Log.DebugMode)
                PF_Log.Message($"Loneliness letter sent for colonist {candidate.LabelShort}");
        }

        // Pools candidates from every player home map before rolling, so the
        // "one wanderlust request per check" cap is global. It used to run
        // per map, which let each base send its own letter on the same tick -
        // two or three wanderlust letters arriving at once in multi-base saves.
        private void CheckWanderlustRequest()
        {
            if (!PeacefulFarewellMod.Settings.wanderlustEnabled)
            {
                return;
            }

            List<Pawn> candidates = new List<Pawn>();
            foreach (Map map in Find.Maps)
            {
                candidates.AddRange(FarewellUtility.FindWanderlustCandidates(map));
            }
            if (candidates.Count == 0)
            {
                return;
            }

            float chance = PeacefulFarewellMod.Settings.wanderlustChancePerCheck;
            foreach (Pawn pawn in candidates.InRandomOrder())
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

            if (PF_Log.DebugMode)
                PF_Log.Message($"Wanderlust letter sent for colonist {pawn.LabelShort}");
        }
    }
}
