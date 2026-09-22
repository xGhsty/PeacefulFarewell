using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // Incremental version of what used to be FarewellUtility.FindLonelyColonists.
    // Evaluates the same O(n^2) pawn-pair opinions and applies the exact same
    // thresholds, but lets the caller feed it a budget of pair evaluations per
    // Step() call instead of doing everything in one go.
    public class PF_LonelinessScan
    {
        public readonly Map Map;
        public List<Pawn> Results { get; private set; }

        private readonly List<Pawn> colonists;
        private readonly float avgThreshold;
        private readonly float maxSingleThreshold;

        private int pawnIndex;
        private int otherIndex;
        private long total;
        private int maxOpinion;
        private int otherCount;
        private bool disqualified;

        private PF_LonelinessScan(Map map, List<Pawn> colonists)
        {
            Map = map;
            this.colonists = colonists;
            avgThreshold = PeacefulFarewellMod.Settings.lonelinessAvgOpinionThreshold;
            maxSingleThreshold = PeacefulFarewellMod.Settings.lonelinessMaxSingleOpinionThreshold;
            Results = new List<Pawn>();
            ResetPairState();
        }

        public static PF_LonelinessScan Start(Map map)
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

            return new PF_LonelinessScan(map, colonists);
        }

        private void ResetPairState()
        {
            total = 0;
            maxOpinion = int.MinValue;
            otherCount = 0;
            disqualified = false;
            otherIndex = 0;
        }

        // Returns true once the whole scan has finished and Results is final.
        public bool Step(int budget)
        {
            while (pawnIndex < colonists.Count)
            {
                Pawn pawn = colonists[pawnIndex];

                if (!pawn.DevelopmentalStage.Adult())
                {
                    AdvanceToNextPawn();
                    continue;
                }

                while (otherIndex < colonists.Count)
                {
                    if (budget <= 0)
                    {
                        return false;
                    }

                    Pawn other = colonists[otherIndex];
                    otherIndex++;

                    if (other == pawn)
                    {
                        continue;
                    }

                    budget--;

                    int opinion = other.relations.OpinionOf(pawn);
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
                        break;
                    }
                }

                if (otherIndex >= colonists.Count || disqualified)
                {
                    if (!disqualified && otherCount > 0)
                    {
                        float avgOpinion = (float)total / otherCount;
                        if (avgOpinion < avgThreshold)
                        {
                            Results.Add(pawn);
                        }
                    }

                    AdvanceToNextPawn();
                }
            }

            if (PeacefulFarewellMod.Settings.debugMode)
            {
                PF_Log.Message($"PF_LonelinessScan on {Map}: colonistsChecked={colonists.Count}, options={Results.Count}, avgThreshold={avgThreshold}, maxSingleThreshold={maxSingleThreshold}");
            }

            return true;
        }

        private void AdvanceToNextPawn()
        {
            pawnIndex++;
            ResetPairState();
        }
    }
}
