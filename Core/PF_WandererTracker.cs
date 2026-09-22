using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PeacefulFarewell
{
    public enum PF_WandererReason : byte
    {
        Loneliness,
        Wanderlust
    }

    public class PF_WandererTracker : WorldComponent
    {
        private const int CheckIntervalTicks = 2500;

        private List<Pawn> wanderers = new List<Pawn>();
        // When each pawn started wandering (Register() time). Drives
        // ResolveChance below - the whole outcome-check model is a per-check
        // probability roll based on how long a pawn has been tracked, not a
        // single random deadline.
        private List<int> registerTick = new List<int>();
        private List<PF_WandererReason> reasons = new List<PF_WandererReason>();
        // The map each wanderer actually left from, so HadPoorColonyRelations can
        // judge them against the colonists who actually know them instead of
        // pooling opinions from every player map (which dilutes the result in
        // multi-colony games).
        // Can be null (e.g. the origin map got abandoned/destroyed after the pawn
        // left) - callers must handle that by falling back to "relations unknown".
        private List<Map> originMaps = new List<Map>();

        // Pawns who settled permanently with a friendly faction (JoinRandomFaction
        // outcome only - a pawn who joined a hostile faction has no reason to write
        // home). Tracked separately from `wanderers` above: those pawns' fates are
        // still unresolved, these are done and just occasionally send word back.
        // Kept forever rather than pruned after some number of letters - the list
        // only grows by one entry per resolved wanderer over the whole game, so the
        // cost of never trimming it is negligible.
        private List<Pawn> settledPawns = new List<Pawn>();
        private List<Map> settledOriginMaps = new List<Map>();
        private List<int> nextLetterCheckTick = new List<int>();
        // The tick this pawn originally left the colony (their Register() time,
        // not when they settled) - kept so the overview/report can show "left
        // on <date>" for settled pawns too, not just still-wandering ones.
        private List<int> settledDepartureTick = new List<int>();

        // Per-save random offset added to TicksGame before the % CheckIntervalTicks
        // test in WorldComponentTick, so the check's phase isn't pinned to
        // multiples of CheckIntervalTicks in absolute game time. Without this,
        // any save whose TicksGame happens to land on such a multiple (which
        // includes every exact day boundary, since a day is 60000 ticks - a
        // multiple of 2500) fires the check on the very first tick after
        // loading, every time. Rolled once and persisted so it stays stable
        // for the life of the save rather than re-randomizing (and briefly
        // double-firing near the boundary) on every load.
        private int checkPhaseOffset = -1;

        public PF_WandererTracker(World world) : base(world)
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

            Scribe_Collections.Look(ref wanderers, "wanderers", LookMode.Reference);
            Scribe_Collections.Look(ref registerTick, "registerTick", LookMode.Value);
            Scribe_Collections.Look(ref reasons, "reasons", LookMode.Value);
            Scribe_Collections.Look(ref originMaps, "originMaps", LookMode.Reference);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (wanderers == null)
                {
                    wanderers = new List<Pawn>();
                }
                if (registerTick == null)
                {
                    registerTick = new List<int>();
                }
                if (reasons == null)
                {
                    reasons = new List<PF_WandererReason>();
                }
                if (originMaps == null)
                {
                    originMaps = new List<Map>();
                }
                // Pre-existing saves from before registerTick was added (or from
                // before this file's nextCheckTick-based model, whose field is no
                // longer read) can't recover the real start time - backfill with
                // now, giving those pawns one full min-max window from this load
                // rather than being immediately treated as past the max.
                while (registerTick.Count < wanderers.Count)
                {
                    registerTick.Add(Find.TickManager.TicksGame);
                }
                while (reasons.Count < wanderers.Count)
                {
                    reasons.Add(PF_WandererReason.Loneliness);
                }
                // Pre-existing saves from before originMaps was added won't have an
                // entry per wanderer - pad with null, which HadPoorColonyRelations
                // treats as "origin unknown" and falls back to pooling every map.
                while (originMaps.Count < wanderers.Count)
                {
                    originMaps.Add(null);
                }
            }

            Scribe_Collections.Look(ref settledPawns, "settledPawns", LookMode.Reference);
            Scribe_Collections.Look(ref settledOriginMaps, "settledOriginMaps", LookMode.Reference);
            Scribe_Collections.Look(ref nextLetterCheckTick, "nextLetterCheckTick", LookMode.Value);
            Scribe_Collections.Look(ref settledDepartureTick, "settledDepartureTick", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (settledPawns == null)
                {
                    settledPawns = new List<Pawn>();
                }
                if (settledOriginMaps == null)
                {
                    settledOriginMaps = new List<Map>();
                }
                if (nextLetterCheckTick == null)
                {
                    nextLetterCheckTick = new List<int>();
                }
                if (settledDepartureTick == null)
                {
                    settledDepartureTick = new List<int>();
                }
                while (settledOriginMaps.Count < settledPawns.Count)
                {
                    settledOriginMaps.Add(null);
                }
                while (nextLetterCheckTick.Count < settledPawns.Count)
                {
                    nextLetterCheckTick.Add(Find.TickManager.TicksGame + RollNextLetterIntervalTicks());
                }
                // Pre-existing saves from before this field was added can't recover
                // the real departure time - backfill with now rather than leaving
                // the overview showing a bogus date.
                while (settledDepartureTick.Count < settledPawns.Count)
                {
                    settledDepartureTick.Add(Find.TickManager.TicksGame);
                }
            }
        }

        // Registers a pawn who just settled permanently with a friendly faction as
        // a source of future "letter from afar" checks. Called once, right when
        // JoinRandomFaction resolves - not for ReturnToColony (they're home, no
        // letter needed) and not for JoinHostileFaction (no reason for someone who
        // defected to a hostile faction to write home, and NotifyLovedOnesAboutLostWanderer
        // already covers that case with its own, deliberately grimmer thoughts).
        private void RegisterSettled(Pawn pawn, Map originMap, int departureTick)
        {
            settledPawns.Add(pawn);
            settledOriginMaps.Add(originMap);
            nextLetterCheckTick.Add(Find.TickManager.TicksGame + RollNextLetterIntervalTicks());
            settledDepartureTick.Add(departureTick);
        }

        public void Register(Pawn pawn, PF_WandererReason reason, Map originMap)
        {
            wanderers.Add(pawn);
            registerTick.Add(Find.TickManager.TicksGame);
            reasons.Add(reason);
            originMaps.Add(originMap);
        }

        // True while a pawn's fate is still unresolved out in the world (they've
        // left the colony but haven't returned, joined a faction, or been lost
        // yet) - used to show "Missing" instead of a faction/situation label in
        // the relations UI for as long as that's the case.
        public bool IsTracked(Pawn pawn)
        {
            return wanderers.Contains(pawn);
        }

        // Read-only snapshot of every wanderer currently tracked, for display
        // in Dialog_PF_WandererOverview and PF_ReportGenerator. Returns plain
        // data (not formatted strings) so each caller can lay it out
        // differently - the in-game window needs a Pawn reference to jump to,
        // the text report just needs a line.
        public struct WandererOverviewEntry
        {
            public Pawn Pawn;
            public PF_WandererReason Reason;
            public float DaysTracked;
            public float ResolveChancePercent;
            public bool ForceDecisionNext;
            public int DepartureTick;
            public int DepartureTile;
        }

        public List<WandererOverviewEntry> GetOverviewEntries()
        {
            var entries = new List<WandererOverviewEntry>();
            for (int i = 0; i < wanderers.Count; i++)
            {
                Pawn pawn = wanderers[i];
                if (pawn == null || pawn.Discarded)
                {
                    continue;
                }

                entries.Add(new WandererOverviewEntry
                {
                    Pawn = pawn,
                    Reason = reasons[i],
                    DaysTracked = (float)(Find.TickManager.TicksGame - registerTick[i]) / GenDate.TicksPerDay,
                    ResolveChancePercent = ResolveChance(i) * 100f,
                    ForceDecisionNext = PastMaxCheckDays(i),
                    DepartureTick = registerTick[i],
                    DepartureTile = DepartureTileFor(originMaps[i])
                });
            }
            return entries;
        }

        // Best-effort tile to anchor the departure date's calendar readout to
        // (RimWorld's date formatting is longitude-dependent) - the map a
        // wanderer actually left from if it's still around, else whatever
        // player home map still exists, else the world's first player
        // settlement. Never returns a tile that would throw; callers fall
        // back to a tile-less "days ago" readout if this returns -1.
        private static int DepartureTileFor(Map originMap)
        {
            if (originMap != null && !originMap.Disposed)
            {
                return originMap.Tile;
            }
            Map anyHome = Find.AnyPlayerHomeMap;
            if (anyHome != null)
            {
                return anyHome.Tile;
            }
            Settlement playerSettlement = Find.WorldObjects?.Settlements?.FirstOrDefault((Settlement s) => s.Faction == Faction.OfPlayer);
            return playerSettlement?.Tile ?? -1;
        }

        // Read-only snapshot of pawns who settled permanently with a friendly
        // faction and occasionally write home, for the same overview/report
        // consumers as GetOverviewEntries above.
        public struct SettledOverviewEntry
        {
            public Pawn Pawn;
            public float DaysUntilNextLetterCheck;
            public int DepartureTick;
            public int DepartureTile;
        }

        public List<SettledOverviewEntry> GetSettledOverviewEntries()
        {
            var entries = new List<SettledOverviewEntry>();
            for (int i = 0; i < settledPawns.Count; i++)
            {
                Pawn pawn = settledPawns[i];
                if (pawn == null || pawn.Discarded || pawn.Dead)
                {
                    continue;
                }

                entries.Add(new SettledOverviewEntry
                {
                    Pawn = pawn,
                    DaysUntilNextLetterCheck = (float)(nextLetterCheckTick[i] - Find.TickManager.TicksGame) / GenDate.TicksPerDay,
                    DepartureTick = settledDepartureTick[i],
                    DepartureTile = DepartureTileFor(settledOriginMaps[i])
                });
            }
            return entries;
        }

        public override void WorldComponentTick()
        {
            if ((Find.TickManager.TicksGame + checkPhaseOffset) % CheckIntervalTicks != 0)
            {
                return;
            }

            try
            {
                DoCheck();
            }
            catch (System.Exception ex)
            {
                PF_Log.Error("Exception in PF_WandererTracker.WorldComponentTick: " + ex);
            }
        }

        private void DoCheck()
        {
            RecoverLostDepartedPawns();
            CheckLettersFromAfar();

            for (int i = wanderers.Count - 1; i >= 0; i--)
            {
                Pawn pawn = wanderers[i];

                if (pawn == null || pawn.Discarded)
                {
                    RemoveAt(i);
                    continue;
                }

                // Something outside our control (e.g. another mod picking a factionless
                // world pawn for its own purposes) can assign this pawn a faction before
                // we ever roll their outcome. If that happens before wandererMinCheckDays
                // has even elapsed, undo it and keep waiting - the whole point of the
                // min-days setting is that the player gets that long before anything can
                // be decided, and PastMaxCheckDays already caps the other end so this
                // can't stall a pawn forever. Once the minimum has elapsed, treat it like
                // before: stop tracking them and let the player know, since at that point
                // "someone else decided for them" is indistinguishable from a valid
                // outcome we just didn't happen to roll ourselves.
                if (pawn.Faction != null)
                {
                    float minDays = PeacefulFarewellMod.Settings.wandererMinCheckDays;
                    int minTicks = (int)(minDays * GenDate.TicksPerDay);
                    if (Find.TickManager.TicksGame < registerTick[i] + minTicks)
                    {
                        if (PeacefulFarewellMod.Settings.debugMode)
                        {
                            PF_Log.Message($"Wanderer {pawn.LabelShort} was assigned faction {pawn.Faction.Name} outside PF_WandererTracker before wandererMinCheckDays elapsed - reverting to factionless and continuing to wait.");
                        }
                        pawn.SetFaction(null);
                        continue;
                    }

                    bool hostile = pawn.Faction.HostileTo(Faction.OfPlayer);
                    bool poorRelations = hostile && HadPoorColonyRelations(pawn, originMaps[i]);

                    if (PeacefulFarewellMod.Settings.debugMode)
                    {
                        PF_Log.Message($"Wanderer {pawn.LabelShort} already has faction {pawn.Faction.Name} (assigned outside PF_WandererTracker, hostile={hostile}, poorRelations={poorRelations}) - stopping tracking without rerolling.");
                    }

                    if (PeacefulFarewellMod.Settings.wandererJoinedFactionLetterEnabled)
                    {
                        if (poorRelations)
                        {
                            // Mirror JoinHostileFaction - bad enough relations that this
                            // reads as a willing choice, so the player is told outright.
                            Find.LetterStack.ReceiveLetter(
                                "PF_WandererJoinedHostileFactionLabel".Translate(),
                                PF_Text.Variant("PF_WandererJoinedHostileFactionText", 4, pawn.Named("PAWN"), pawn.Faction.Name),
                                LetterDefOf.NegativeEvent);
                        }
                        else if (hostile)
                        {
                            // Good standing with the colony - reads as a capture instead.
                            // Deliberately vague, same as JoinHostileFaction.
                            Find.LetterStack.ReceiveLetter(
                                "PF_WandererMissingLabel".Translate(),
                                PF_Text.Variant("PF_WandererMissingText", 5, pawn.Named("PAWN")),
                                LetterDefOf.NegativeEvent);
                        }
                        else
                        {
                            Find.LetterStack.ReceiveLetter(
                                "PF_WandererJoinedFactionLabel".Translate(),
                                PF_Text.Variant("PF_WandererJoinedFactionText", 4, pawn.Named("PAWN"), pawn.Faction.Name),
                                LetterDefOf.NeutralEvent);
                        }
                    }

                    if (hostile)
                    {
                        FarewellUtility.NotifyLovedOnesAboutLostWanderer(pawn);
                    }
                    else
                    {
                        FarewellUtility.NotifyLovedOnesAboutSafeWanderer(pawn);
                        RegisterSettled(pawn, originMaps[i], registerTick[i]);
                    }

                    RemoveAt(i);
                    continue;
                }

                if (!Rand.Chance(ResolveChance(i)))
                {
                    continue;
                }

                ResolveOutcome(pawn, reasons[i], originMaps[i], i);
            }

            // Logged last, after every correction/resolution above has already
            // run this check - logging first (the previous order) captured a
            // pawn's faction mid-flight, e.g. right after vanilla's redress
            // system assigned one out-of-band but before the loop above had a
            // chance to revert it for still being under wandererMinCheckDays,
            // which made the snapshot look like the min-days setting wasn't
            // being honored when it actually was.
            LogWandererSnapshot();
        }

        // Writes a snapshot of every currently-tracked wanderer to
        // WandererDebugLog.txt so the mod author can see who's still out in
        // the world and roughly when their fate will next be checked, without
        // having to wait for a letter or dig through the mixed-content
        // CustomLog.txt.
        private void LogWandererSnapshot()
        {
            if (PeacefulFarewellMod.Settings == null || !PeacefulFarewellMod.Settings.debugMode)
            {
                return;
            }

            var descriptions = new List<string>();
            for (int i = 0; i < wanderers.Count; i++)
            {
                Pawn pawn = wanderers[i];
                if (pawn == null || pawn.Discarded)
                {
                    // Surfaced instead of silently skipped - a null/Discarded entry
                    // still occupying a slot means the list's true count and the
                    // "currently tracked" count in the header will disagree, which
                    // would otherwise look like an empty tracker even though it isn't.
                    descriptions.Add($"[slot {i}] pawn={(pawn == null ? "null" : "Discarded")} | reason={reasons[i]} - stale/unresolved reference, not cleaned up by DoCheck yet");
                    continue;
                }

                float daysTracked = (float)(Find.TickManager.TicksGame - registerTick[i]) / GenDate.TicksPerDay;
                bool pastMax = PastMaxCheckDays(i);

                descriptions.Add(
                    $"{pawn.LabelShort} | reason={reasons[i]} | trackedFor={daysTracked:F1}d | resolveChance={(ResolveChance(i) * 100f):F0}% | forceDecisionNext={pastMax} | faction={pawn.Faction?.Name ?? "none"}");
            }

            PF_WandererDebugLog.LogSnapshot(wanderers.Count, descriptions);
        }

        // For each settled pawn whose letter cooldown has elapsed, sends their
        // letter home. A pawn with no eligible loved one currently in the
        // colony (TrySendLetterFromAfar returns false) just gets rerolled for
        // next time instead of being dropped - the colony's makeup can change
        // (recruits, returns) so "no one to receive it" isn't a permanent
        // state. No separate per-check chance roll on top of the cooldown -
        // the 5-15 day random interval between checks (RollNextLetterIntervalTicks)
        // is already the randomness; stacking a % roll on top of it only made
        // "next letter in Xd" in the overview misleading (implying the letter
        // is due, when it might silently not happen).
        private void CheckLettersFromAfar()
        {
            if (!PeacefulFarewellMod.Settings.letterFromAfarEnabled)
            {
                return;
            }

            for (int i = settledPawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = settledPawns[i];

                if (pawn == null || pawn.Discarded || pawn.Dead)
                {
                    RemoveSettledAt(i);
                    continue;
                }

                if (Find.TickManager.TicksGame < nextLetterCheckTick[i])
                {
                    continue;
                }

                nextLetterCheckTick[i] = Find.TickManager.TicksGame + RollNextLetterIntervalTicks();

                FarewellUtility.TrySendLetterFromAfar(pawn);
            }
        }

        private void RemoveSettledAt(int index)
        {
            settledPawns.RemoveAt(index);
            settledOriginMaps.RemoveAt(index);
            nextLetterCheckTick.RemoveAt(index);
            settledDepartureTick.RemoveAt(index);
        }

        // Wide window (5-15 days) between letter rolls per pawn - letters are
        // meant to be a rare, pleasant surprise, not a recurring notification.
        private int RollNextLetterIntervalTicks()
        {
            return (int)(Rand.Range(5f, 15f) * GenDate.TicksPerDay);
        }

        // Safety net for pawns whose departure job got interrupted after
        // clearing their Faction but before reaching the toil that calls
        // Register() - e.g. a save/load or an external interruption mid-exit.
        // Such a pawn has a lingering SaveWorkAndSchedule snapshot (proof they
        // left through this mod and haven't been restored/returned yet) but
        // isn't in `wanderers`, so PF_WandererTracker never rolls their fate
        // and the player never gets a letter - they just silently vanish,
        // and worse, a factionless pawn sitting untracked is fair game for
        // vanilla's PawnGenerator "redress" system to hand to a random
        // faction (see Patch_IsValidCandidateToRedress) without this mod ever
        // knowing. Re-registering them here re-establishes IsTracked, which
        // blocks further redress and puts them back through the normal
        // resolve-outcome cycle so the player eventually gets a real letter.
        private void RecoverLostDepartedPawns()
        {
            foreach (Pawn pawn in PF_GameComponent.GetPawnsWithPendingDeparture())
            {
                if (pawn == null || pawn.Discarded || pawn.Dead)
                {
                    continue;
                }

                if (IsTracked(pawn))
                {
                    continue;
                }

                // Already back with the player (e.g. RestoreWorkAndSchedule hasn't
                // run yet this tick but SetFaction already happened) - not lost.
                if (pawn.Faction == Faction.OfPlayer && pawn.Spawned)
                {
                    continue;
                }

                if (pawn.Faction != null)
                {
                    // Already settled somewhere (most likely picked up by vanilla's
                    // redress system while untracked, see the comment above this
                    // method) - treat that as a fact of life rather than undoing
                    // it. Just stop counting them as "pending departure" so they
                    // don't keep tripping this sweep every check forever.
                    PF_Log.Warning($"Found untracked departed pawn {pawn.LabelShort} who already has faction {pawn.Faction.Name} - leaving as-is (not reverting), clearing stale pending-departure state.");
                    PF_WandererDebugLog.LogEvent($"{pawn.LabelShort} was untracked but already settled with faction {pawn.Faction.Name} - left as-is, pending-departure snapshot discarded.");
                    PF_GameComponent.DiscardPendingDeparture(pawn);
                    continue;
                }

                // Genuinely factionless and untracked - a departure job was
                // interrupted before it could Register(). Re-register now so
                // their fate resolves normally instead of leaving them exposed
                // to vanilla's redress system with the player never finding out.
                PF_Log.Warning($"Recovered lost departed pawn {pawn.LabelShort} - had a pending departure snapshot but was not tracked by PF_WandererTracker (interrupted departure job?). Re-registering so their fate can resolve normally.");
                PF_WandererDebugLog.LogEvent($"RECOVERED lost pawn {pawn.LabelShort} - was factionless and untracked despite a pending departure snapshot. Re-registered as Loneliness.");

                Register(pawn, PF_WandererReason.Loneliness, pawn.Map);
            }
        }

        private void RemoveAt(int index)
        {
            wanderers.RemoveAt(index);
            registerTick.RemoveAt(index);
            reasons.RemoveAt(index);
            originMaps.RemoveAt(index);
        }

        // The chance this pawn's fate gets resolved on the current check, given
        // how long they've been tracked. Rises linearly from a small floor
        // (5%) right as wandererMinCheckDays elapses, up to a hard 100% once
        // wandererMaxCheckDays elapses - a pawn can never resolve before the
        // min and is always resolved by the max, same guarantees the old
        // single-roll-wait model gave, but as a rising-probability curve
        // in between rather than one random deadline.
        private float ResolveChance(int index)
        {
            float minDays = PeacefulFarewellMod.Settings.wandererMinCheckDays;
            float maxDays = PeacefulFarewellMod.Settings.wandererMaxCheckDays;
            float daysTracked = (float)(Find.TickManager.TicksGame - registerTick[index]) / GenDate.TicksPerDay;

            if (daysTracked < minDays)
            {
                return 0f;
            }
            if (daysTracked >= maxDays)
            {
                return 1f;
            }

            // Guards the maxDays == minDays case (sliders allow it) from a
            // divide-by-zero - already covered by the daysTracked >= maxDays
            // check above whenever the window is zero-width, but kept explicit
            // for clarity.
            float windowDays = maxDays - minDays;
            if (windowDays <= 0f)
            {
                return 1f;
            }

            const float FloorChance = 0.05f;
            float t = (daysTracked - minDays) / windowDays;
            return FloorChance + (1f - FloorChance) * t;
        }

        // Once wandererMaxCheckDays has elapsed since Register(), ResolveChance
        // has saturated to 100% - the pawn's fate is guaranteed to be decided
        // this check. Kept as a named wrapper since callers (the debug log,
        // the overview UI) care about this specific threshold rather than the
        // chance value itself.
        private bool PastMaxCheckDays(int index)
        {
            return ResolveChance(index) >= 1f;
        }

        private void ResolveOutcome(Pawn pawn, PF_WandererReason reason, Map originMap, int index)
        {
            if (reason == PF_WandererReason.Wanderlust)
            {
                ResolveWanderlustOutcome(pawn, originMap, index);
                return;
            }

            // Whether to resolve at all was already decided by the ResolveChance
            // roll in DoCheck - reaching here means this pawn's fate IS being
            // decided now, so this is purely return-vs-join, no "keep wandering".
            int outcome = Rand.RangeInclusive(0, 1);

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Wanderer outcome check for {pawn.LabelShort}: rolled {outcome} (0=return, 1=join faction)");
            }

            switch (outcome)
            {
                case 0:
                    ReturnToColony(pawn, giveGreatJourneyThought: false, originMap, registerTick[index]);
                    break;
                default:
                    JoinRandomFaction(pawn, PF_WandererReason.Loneliness, originMap, registerTick[index]);
                    break;
            }

            RemoveAt(index);
        }

        private void ResolveWanderlustOutcome(Pawn pawn, Map originMap, int index)
        {
            int outcome = Rand.RangeInclusive(0, 2);

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Wanderlust outcome check for {pawn.LabelShort}: rolled {outcome} (0=return, 1=join friendly, 2=join hostile)");
            }

            switch (outcome)
            {
                case 0:
                    ReturnToColony(pawn, giveGreatJourneyThought: true, originMap, registerTick[index]);
                    break;
                case 1:
                    JoinRandomFaction(pawn, PF_WandererReason.Wanderlust, originMap, registerTick[index]);
                    break;
                default:
                    JoinHostileFaction(pawn, originMap, registerTick[index]);
                    break;
            }

            RemoveAt(index);
        }

        private void ReturnToColony(Pawn pawn, bool giveGreatJourneyThought, Map originMap, int originalRegisterTick)
        {
            Map map = (originMap != null && originMap.IsPlayerHome) ? originMap : Find.AnyPlayerHomeMap;
            if (map == null)
            {
                // Keeps the original registerTick so ResolveChance doesn't reset -
                // this is a retry of the same resolution, not a fresh wander.
                registerTick.Add(originalRegisterTick);
                wanderers.Add(pawn);
                reasons.Add(giveGreatJourneyThought ? PF_WandererReason.Wanderlust : PF_WandererReason.Loneliness);
                originMaps.Add(originMap);
                PF_WandererDebugLog.LogEvent($"{pawn.LabelShort} rolled 'return to colony' but no player home map exists - requeued.");
                return;
            }

            PF_WandererDebugLog.LogEvent($"{pawn.LabelShort} is RETURNING TO COLONY (wanderlust={giveGreatJourneyThought}).");

            Find.WorldPawns.RemovePawn(pawn);

            // RandomEdgeCell alone can land in solid rock on mountainous maps,
            // spawning the pawn sealed inside the mountain with no way out -
            // require the cell to actually be walkable and reachable.
            if (!CellFinder.TryFindRandomEdgeCellWith(
                c => c.Standable(map) && c.Walkable(map) && !c.Fogged(map) && map.reachability.CanReachColony(c),
                map,
                CellFinder.EdgeRoadChance_Ignore,
                out IntVec3 cell))
            {
                cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 50);
            }
            pawn.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(pawn, cell, map);

            // SetFaction(Faction.OfPlayer) just reset work priorities and
            // schedule to vanilla defaults (same as a fresh recruit) - restore
            // whatever was saved when this pawn left, if anything.
            PF_GameComponent.RestoreWorkAndSchedule(pawn);

            FarewellUtility.RefreshApparelAfterJourney(pawn);

            if (giveGreatJourneyThought)
            {
                if (pawn.needs?.mood != null)
                {
                    pawn.needs.mood.thoughts.memories.TryGainMemory(PF_ThoughtDefOf.PF_WanderlustGreatJourney);
                }
                FarewellUtility.NotifyFamilyAboutWanderlustReturn(pawn);
                // Having seen the world and chosen to come back, they're no longer
                // a candidate for another Wanderlust request - see IsWanderlustEligible.
                PF_GameComponent.NotifyReturnedFromWanderlust(pawn);
            }
            else
            {
                // Shield them from being flagged as lonely again immediately - give
                // the player a real chance to fix their relations first.
                PF_GameComponent.NotifyReturnedFromLoneliness(pawn);
            }

            Find.LetterStack.ReceiveLetter(
                "PF_WandererReturnedLabel".Translate(),
                PF_Text.Variant("PF_WandererReturnedText", 4, pawn.Named("PAWN")),
                LetterDefOf.NeutralEvent,
                pawn);
        }

        // Large-but-not-absolute weight multiplier applied to a candidate faction
        // that has a pawn with a positive/neutral family or partner relation to
        // the departing wanderer - per user request, a wanderer with a sibling
        // (etc) in a friendly faction should be much more likely to end up there,
        // without making it a certainty (other factions keep a real, if small,
        // chance too).
        //
        // Scaled down by StrongFriendsInColony below - a flat x8 made a pawn with
        // one distant relative in a faction favor leaving several close friends
        // behind in the colony for them, which reads as illogical (confirmed by
        // user: colonist with more friends in the colony still preferred a
        // faction where they had just one other friend). The more close bonds a
        // pawn is actually leaving behind, the less a single outside relation
        // should dominate the roll.
        private const float FamilyFactionWeightMultiplier = 8f;

        // Floor the scaled-down multiplier can't drop below - per user request,
        // an actual family/partner tie should stay clearly stronger than a mere
        // colony friendship even when the pawn leaves several close friends
        // behind, so this sits well above 1x (parity with a random faction)
        // rather than letting enough friends fully cancel it out.
        private const float MinFamilyFactionWeightMultiplier = 3f;

        // How many close, mutual friendships the departing pawn is leaving
        // behind in the colony they actually left from - used to dilute
        // FamilyFactionWeightMultiplier so a wanderer with many strong bonds at
        // home isn't overwhelmingly drawn to a faction over just one relative.
        //
        // Uses FarewellUtility.RealFriendOpinionOf (memory-based) rather than
        // raw Pawn_RelationsTracker.OpinionOf - OpinionOf also folds in purely
        // situational/passive social thoughts (e.g. a beauty-related trait's
        // effect on how everyone perceives that pawn), which would count a
        // colonist as a "strong friend" of everyone around them regardless of
        // any actual history, inflating this count for reasons unrelated to a
        // real bond.
        private static int CountStrongFriendsInColony(Pawn pawn, Map originMap)
        {
            if (pawn.relations == null || originMap == null || !originMap.IsPlayerHome)
            {
                return 0;
            }

            int count = 0;
            foreach (Pawn other in originMap.mapPawns.FreeColonistsSpawned)
            {
                if (other == pawn || other.Dead || other.relations == null)
                {
                    continue;
                }

                if (FarewellUtility.RealFriendOpinionOf(pawn, other) >= FarewellUtility.StrongFriendOpinionThreshold
                    && FarewellUtility.RealFriendOpinionOf(other, pawn) >= FarewellUtility.StrongFriendOpinionThreshold)
                {
                    count++;
                }
            }

            return count;
        }

        // Excludes factions with no real presence on the world map (e.g. vanilla
        // Beggars/Pilgrims - already caught by def.hidden above, but this also
        // covers modded transient factions like vampire/sanguophage covens that
        // don't set FactionDef.hidden but likewise never own a Settlement).
        // A wanderer "joining" such a faction would have nowhere to actually go.
        private static bool HasAnySettlement(Faction faction)
        {
            List<Settlement> settlements = Find.WorldObjects.Settlements;
            for (int i = 0; i < settlements.Count; i++)
            {
                if (settlements[i].Faction == faction)
                {
                    return true;
                }
            }
            return false;
        }

        private void JoinRandomFaction(Pawn pawn, PF_WandererReason reason, Map originMap, int originalRegisterTick)
        {
            List<Faction> candidates = Find.FactionManager.AllFactions
                .Where((Faction f) => !f.IsPlayer && !f.Hidden && !f.def.hidden && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer) && HasAnySettlement(f))
                .ToList();

            if (candidates.Count == 0)
            {
                // Requeue under the pawn's original reason - a Wanderlust departure
                // that fails to find any faction candidates should keep rolling on
                // ResolveWanderlustOutcome's outcome switch, not silently become a
                // Loneliness pawn and lose its return-thought/notification flavor.
                // Keeps the original registerTick so ResolveChance doesn't reset -
                // this is a retry of the same resolution, not a fresh wander.
                registerTick.Add(originalRegisterTick);
                wanderers.Add(pawn);
                reasons.Add(reason);
                originMaps.Add(originMap);
                PF_WandererDebugLog.LogEvent($"{pawn.LabelShort} rolled 'join friendly faction' but no candidates exist - requeued.");
                return;
            }

            HashSet<Faction> familyFactions = FindFamilyFactions(pawn, candidates);

            int strongFriendsInColony = CountStrongFriendsInColony(pawn, originMap);
            float familyWeight = FamilyFactionWeightMultiplier;
            if (strongFriendsInColony > 0)
            {
                familyWeight = System.Math.Max(MinFamilyFactionWeightMultiplier, FamilyFactionWeightMultiplier / (strongFriendsInColony + 1));
            }

            Faction targetFaction = candidates.RandomElementByWeight(
                (Faction f) => familyFactions.Contains(f) ? familyWeight : 1f);

            if (PeacefulFarewellMod.Settings.debugMode && familyFactions.Count > 0)
            {
                PF_Log.Message($"JoinRandomFaction for {pawn.LabelShort}: family/partner found in faction(s) [{string.Join(", ", familyFactions.Select(f => f.Name))}], strongFriendsInColony={strongFriendsInColony}, familyWeight={familyWeight}, rolled {targetFaction.Name}");
            }

            pawn.SetFaction(targetFaction);
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            PF_WandererDebugLog.LogEvent($"{pawn.LabelShort} FOUND A NEW HOME with faction {targetFaction.Name}.");

            FarewellUtility.NotifyLovedOnesAboutSafeWanderer(pawn);
            RegisterSettled(pawn, originMap, originalRegisterTick);

            if (PeacefulFarewellMod.Settings.wandererJoinedFactionLetterEnabled)
            {
                Find.LetterStack.ReceiveLetter(
                    "PF_WandererJoinedFactionLabel".Translate(),
                    PF_Text.Variant("PF_WandererJoinedFactionText", 4, pawn.Named("PAWN"), targetFaction.Name),
                    LetterDefOf.NeutralEvent);

                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"Wanderer {pawn.LabelShort} joined faction {targetFaction.Name}, letter sent");
                }
            }
            else if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Wanderer {pawn.LabelShort} joined faction {targetFaction.Name}, letter disabled in settings");
            }
        }

        private void JoinHostileFaction(Pawn pawn, Map originMap, int originalRegisterTick)
        {
            List<Faction> candidates = Find.FactionManager.AllFactions
                .Where((Faction f) => !f.IsPlayer && !f.Hidden && !f.def.hidden && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer) && HasAnySettlement(f))
                .ToList();

            if (candidates.Count == 0)
            {
                // No hostile faction available right now - fall back to the friendly path
                // instead of stalling this pawn forever. JoinHostileFaction is only ever
                // called from ResolveWanderlustOutcome, so this pawn's reason is Wanderlust.
                JoinRandomFaction(pawn, PF_WandererReason.Wanderlust, originMap, originalRegisterTick);
                return;
            }

            Faction targetFaction = candidates.RandomElement();
            bool poorRelations = HadPoorColonyRelations(pawn, originMap);
            pawn.SetFaction(targetFaction);
            Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.KeepForever);

            PF_WandererDebugLog.LogEvent($"{pawn.LabelShort} joined HOSTILE faction {targetFaction.Name} (poorRelations={poorRelations}).");

            FarewellUtility.NotifyLovedOnesAboutLostWanderer(pawn);

            if (PeacefulFarewellMod.Settings.wandererJoinedFactionLetterEnabled)
            {
                if (poorRelations)
                {
                    // Bad enough relations that this reads as a willing choice - the
                    // player gets told outright, and warned they may meet again.
                    Find.LetterStack.ReceiveLetter(
                        "PF_WandererJoinedHostileFactionLabel".Translate(),
                        PF_Text.Variant("PF_WandererJoinedHostileFactionText", 4, pawn.Named("PAWN"), targetFaction.Name),
                        LetterDefOf.NegativeEvent);
                }
                else
                {
                    // Good standing with the colony - a willing defection wouldn't make
                    // sense, so this reads as a capture instead. Deliberately vague -
                    // the player never learns exactly what happened to them.
                    Find.LetterStack.ReceiveLetter(
                        "PF_WandererMissingLabel".Translate(),
                        PF_Text.Variant("PF_WandererMissingText", 5, pawn.Named("PAWN")),
                        LetterDefOf.NegativeEvent);
                }
            }

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"Wanderer {pawn.LabelShort} joined hostile faction {targetFaction.Name}, poorRelations={poorRelations}, letter sent={PeacefulFarewellMod.Settings.wandererJoinedFactionLetterEnabled}");
            }
        }

        // Which of the candidate factions the departing pawn has a positive/
        // neutral family or partner relation in - used by JoinRandomFaction to
        // weight faction selection so a wanderer with (e.g.) a sibling in a
        // friendly faction is much more likely to end up there, per user
        // request. Only formal family/partner relations count (not plain
        // friendship/opinion), and mirrors FindClosestRelation's rule that a
        // relative with a negative opinion (e.g. a misogynist trait souring an
        // otherwise-positive Sibling relation) doesn't count - see
        // FarewellUtility.MinRelativeOpinionToFollow.
        //
        // pawn.relations.RelatedPawns (vanilla) is the right source here rather
        // than scanning DirectRelations or every world pawn ourselves: it walks
        // the full relation graph (direct + virtual, so Sibling is included
        // even without a physical DirectRelation entry - see the Bug 1 note in
        // memory about that gap) and is normally a small set, cheap to iterate
        // once per faction-join resolution.
        private static HashSet<Faction> FindFamilyFactions(Pawn pawn, List<Faction> candidates)
        {
            var result = new HashSet<Faction>();
            if (pawn.relations == null)
            {
                return result;
            }

            foreach (Pawn related in pawn.relations.RelatedPawns)
            {
                if (related == null || related.Faction == null || !candidates.Contains(related.Faction))
                {
                    continue;
                }
                if (result.Contains(related.Faction))
                {
                    continue;
                }

                bool isFamily = false;
                foreach (PawnRelationDef relationDef in PawnRelationUtility.GetRelations(pawn, related))
                {
                    if (FarewellUtility.StayForFamilyRelations.Contains(relationDef))
                    {
                        isFamily = true;
                        break;
                    }
                }

                if (isFamily && pawn.relations.OpinionOf(related) >= FarewellUtility.MinRelativeOpinionToFollow)
                {
                    result.Add(related.Faction);
                }
            }

            return result;
        }

        // Whether the colony's opinion of this pawn was bad enough that joining a
        // hostile faction plausibly reads as a willing choice rather than a
        // capture. Mirrors PF_LonelinessScan's thresholds so "poor relations"
        // means the same thing everywhere in the mod, but does a single
        // one-shot pass instead of a budgeted multi-tick scan since this only
        // ever runs once, for one pawn, at the moment their outcome resolves.
        // Judges the pawn against the colonists on the map they actually left
        // from (recorded at Register() time) - colonists on an unrelated map in a
        // multi-colony game generally never met this pawn, so including them would
        // dilute the average toward neutral regardless of how the pawn was really
        // regarded. Falls back to pooling every player home map only if the origin
        // map is unknown (old save from before this was tracked) or no longer a
        // player home (e.g. the colony was abandoned or lost after the pawn left).
        private static bool HadPoorColonyRelations(Pawn pawn, Map originMap)
        {
            IEnumerable<Map> mapsToCheck = (originMap != null && originMap.IsPlayerHome)
                ? new List<Map> { originMap }
                : Find.Maps.Where((Map m) => m.IsPlayerHome);

            List<Pawn> others = mapsToCheck
                .SelectMany((Map m) => m.mapPawns.FreeColonistsSpawned)
                .Where((Pawn p) => p != pawn && p.relations != null)
                .ToList();

            if (others.Count == 0)
            {
                return false;
            }

            float maxSingleThreshold = PeacefulFarewellMod.Settings.lonelinessMaxSingleOpinionThreshold;
            float avgThreshold = PeacefulFarewellMod.Settings.lonelinessAvgOpinionThreshold;

            int total = 0;
            foreach (Pawn other in others)
            {
                int opinion = other.relations.OpinionOf(pawn);
                total += opinion;
                if (opinion >= maxSingleThreshold)
                {
                    return false;
                }
            }

            float avgOpinion = (float)total / others.Count;
            return avgOpinion < avgThreshold;
        }
    }
}
