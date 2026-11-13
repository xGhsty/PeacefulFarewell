using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // Incremental version of what used to be FarewellUtility.FindLonelyColonists.
    // Evaluates the same O(n^2) pawn-pair opinions and applies the exact same
    // thresholds, but lets the caller feed it a time budget per Step() call
    // instead of doing everything in one go.
    //
    // OpinionOf is the whole cost (~0.3 ms per call on a ~280-colonist,
    // heavily modded save), so the scan tries to call it as few times as
    // possible without changing the result. A pawn is disqualified the
    // moment ANYONE's opinion of them reaches maxSingleThreshold, so for
    // each pawn the colonists most likely to like them are asked first:
    //   1. whoever disqualified them on the previous scan ("fan" - opinions
    //      drift slowly, so this alone usually settles it in one call),
    //   2. pawns they have a direct relation with (lover, spouse, family),
    //   3. everyone else, in list order.
    // The order only changes how soon the early exit is hit: a pawn who is
    // not disqualified is still evaluated against every other colonist, so
    // the average and the max - and therefore Results - are identical to
    // evaluating in plain list order.
    public class PF_LonelinessScan
    {
        public readonly Map Map;
        public List<Pawn> Results { get; private set; }

        private readonly List<Pawn> colonists;
        private readonly Dictionary<Pawn, int> indexOf;
        private readonly float avgThreshold;
        private readonly float maxSingleThreshold;

        // Pawn -> the colonist whose opinion disqualified them last time.
        // Owned by PF_GameComponent (not saved; it is only a hint).
        private readonly Dictionary<Pawn, Pawn> lastFan;

        private int pawnIndex;

        // Evaluation order for the current pawn: priority indices first,
        // then a linear sweep over everyone, skipping indices already done.
        private readonly List<int> priority = new List<int>();
        private int priorityPos;
        private int sweepIndex;

        // evaluatedStamp[i] == stamp means colonist i was already evaluated
        // for the current pawn - avoids clearing an array per pawn.
        private readonly int[] evaluatedStamp;
        private int stamp;

        private long total;
        private int maxOpinion;
        private int otherCount;
        private bool disqualified;
        private bool pawnStarted;

        // Perf stats for the debug log: total time spent inside Step(),
        // over how many game ticks the scan was spread, and how often the
        // remembered fan settled a pawn in a single call.
        private long elapsedStopwatchTicks;
        private int stepCalls;
        private int evaluations;
        private int fanHits;

        private PF_LonelinessScan(Map map, List<Pawn> colonists, Dictionary<Pawn, Pawn> lastFan)
        {
            Map = map;
            this.colonists = colonists;
            this.lastFan = lastFan;
            avgThreshold = PeacefulFarewellMod.Settings.lonelinessAvgOpinionThreshold;
            maxSingleThreshold = PeacefulFarewellMod.Settings.lonelinessMaxSingleOpinionThreshold;
            Results = new List<Pawn>();

            indexOf = new Dictionary<Pawn, int>(colonists.Count);
            for (int i = 0; i < colonists.Count; i++)
            {
                indexOf[colonists[i]] = i;
            }
            evaluatedStamp = new int[colonists.Count];
        }

        public static PF_LonelinessScan Start(Map map, Dictionary<Pawn, Pawn> lastFan)
        {
            if (map == null || !map.IsPlayerHome)
            {
                return null;
            }

            // Everyone eligible has an opinion that counts towards someone else's
            // average (a child can still dislike a colonist), but only adults can
            // be picked as the one who decides to leave.
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned
                .Where(FarewellUtility.IsEligibleColonist)
                .Where((Pawn p) => p.relations != null)
                .ToList();

            // A pawn who just joined hasn't had time to build real relationships
            // yet, so a low starting opinion (e.g. from an Ugly trait) shouldn't
            // immediately flag them as wanting to leave out of loneliness.
            float minDays = PeacefulFarewellMod.Settings.lonelinessMinDaysInColony;
            if (minDays > 0f)
            {
                int minTicks = (int)(minDays * GenDate.TicksPerDay);
                colonists.RemoveAll((Pawn p) => p.records == null
                    || p.records.GetAsInt(RecordDefOf.TimeAsColonistOrColonyAnimal) < minTicks);
            }

            if (colonists.Count < 2)
            {
                return null;
            }

            return new PF_LonelinessScan(map, colonists, lastFan ?? new Dictionary<Pawn, Pawn>());
        }

        // Returns true once the whole scan has finished and Results is final.
        // Runs until deadlineTimestamp (Stopwatch.GetTimestamp units), but
        // always does at least minEvaluations so a slow machine still
        // finishes eventually. The clock is read before every evaluation:
        // one OpinionOf costs far more than one Stopwatch read, and checking
        // only every few calls let a single tick overrun the budget ~20x.
        public bool Step(long deadlineTimestamp, int minEvaluations)
        {
            long start = Stopwatch.GetTimestamp();
            stepCalls++;
            try
            {
                return StepInternal(deadlineTimestamp, minEvaluations);
            }
            finally
            {
                elapsedStopwatchTicks += Stopwatch.GetTimestamp() - start;
            }
        }

        private bool StepInternal(long deadlineTimestamp, int minEvaluations)
        {
            int done = 0;
            while (pawnIndex < colonists.Count)
            {
                Pawn pawn = colonists[pawnIndex];

                if (!pawn.DevelopmentalStage.Adult())
                {
                    AdvanceToNextPawn();
                    continue;
                }

                if (!pawnStarted)
                {
                    BeginPawn(pawn);
                }

                int other;
                while ((other = NextOtherIndex()) >= 0)
                {
                    if (done >= minEvaluations && Stopwatch.GetTimestamp() >= deadlineTimestamp)
                    {
                        // Put it back - it hasn't been evaluated yet.
                        UndoNextOtherIndex(other);
                        return false;
                    }

                    done++;
                    evaluations++;

                    Pawn otherPawn = colonists[other];
                    int opinion = otherPawn.relations.OpinionOf(pawn);
                    total += opinion;
                    otherCount++;
                    if (opinion > maxOpinion)
                    {
                        maxOpinion = opinion;
                    }

                    // A single opinion above the threshold already disqualifies this
                    // pawn regardless of everyone else, so stop paying for further
                    // OpinionOf calls (each walks the other pawn's full thought list).
                    if (maxOpinion >= maxSingleThreshold)
                    {
                        disqualified = true;
                        if (otherCount == 1 && lastFan.TryGetValue(pawn, out Pawn fan) && fan == otherPawn)
                        {
                            fanHits++;
                        }
                        lastFan[pawn] = otherPawn;
                        break;
                    }
                }

                if (!disqualified)
                {
                    lastFan.Remove(pawn);
                    if (otherCount > 0)
                    {
                        float avgOpinion = (float)total / otherCount;
                        if (avgOpinion < avgThreshold)
                        {
                            Results.Add(pawn);
                        }
                    }
                }

                AdvanceToNextPawn();
            }

            if (PF_Log.DebugMode)
                PF_Log.Message($"PF_LonelinessScan on {Map}: colonistsChecked={colonists.Count}, options={Results.Count}, avgThreshold={avgThreshold}, maxSingleThreshold={maxSingleThreshold}, perf: {evaluations} opinions in {elapsedStopwatchTicks * 1000.0 / Stopwatch.Frequency:F1} ms over {stepCalls} ticks, fanHits={fanHits}");

            return true;
        }

        // Builds the "most likely to like this pawn" list for the pawn about
        // to be evaluated. Cheap: one dictionary lookup plus the pawn's own
        // DirectRelations list - no opinion calls.
        private void BeginPawn(Pawn pawn)
        {
            pawnStarted = true;
            stamp++;
            total = 0;
            maxOpinion = int.MinValue;
            otherCount = 0;
            disqualified = false;
            priority.Clear();
            priorityPos = 0;
            sweepIndex = 0;

            // The pawn never evaluates themselves.
            evaluatedStamp[pawnIndex] = stamp;

            if (lastFan.TryGetValue(pawn, out Pawn fan) && fan != null)
            {
                AddPriority(fan);
            }

            List<DirectPawnRelation> relations = pawn.relations.DirectRelations;
            for (int i = 0; i < relations.Count; i++)
            {
                AddPriority(relations[i].otherPawn);
            }
        }

        private void AddPriority(Pawn other)
        {
            if (other != null && indexOf.TryGetValue(other, out int idx) && evaluatedStamp[idx] != stamp && !priority.Contains(idx))
            {
                priority.Add(idx);
            }
        }

        // Next colonist index to evaluate for the current pawn, or -1 when
        // everyone has been evaluated. Marks the index as evaluated.
        private int NextOtherIndex()
        {
            while (priorityPos < priority.Count)
            {
                int idx = priority[priorityPos++];
                if (evaluatedStamp[idx] != stamp)
                {
                    evaluatedStamp[idx] = stamp;
                    return idx;
                }
            }

            while (sweepIndex < colonists.Count)
            {
                int idx = sweepIndex++;
                if (evaluatedStamp[idx] != stamp)
                {
                    evaluatedStamp[idx] = stamp;
                    return idx;
                }
            }

            return -1;
        }

        // Reverses the last NextOtherIndex() when the time budget ran out
        // before that colonist was actually evaluated.
        private void UndoNextOtherIndex(int idx)
        {
            evaluatedStamp[idx] = stamp - 1;
            if (priorityPos > 0 && priorityPos <= priority.Count && priority[priorityPos - 1] == idx)
            {
                priorityPos--;
            }
            else
            {
                sweepIndex--;
            }
        }

        private void AdvanceToNextPawn()
        {
            pawnIndex++;
            pawnStarted = false;
        }
    }
}
