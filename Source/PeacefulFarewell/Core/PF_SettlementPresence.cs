using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace PeacefulFarewell
{
    // Puts settled former colonists physically on their home settlement's
    // map whenever one gets generated, so they're actually there - fighting
    // alongside the defenders, able to die, be downed, or be captured -
    // instead of only existing as a world pawn the overview claims lives
    // there. Two entry points, because the two ways a settlement map comes
    // into being look different:
    //  - vanilla (player attacks/visits the settlement): the map's parent IS
    //    the Settlement and the defending garrison already exists by the end
    //    of map generation - see Patch_GenerateMap_SettlementResidents.
    //  - Dynamic Diplomacy faction battles: the map belongs to DD's own
    //    MapParentNPCArena on the settlement's tile (combatLoc points at the
    //    settlement) and the defenders are spawned and given their lord only
    //    after map generation, inside InitArenaMap - see
    //    Patch_DD_InitArenaMap_SettlementResidents.
    //
    // Residents join the defenders' lord but are deliberately NOT added to
    // DD's own lhs/rhs pawn lists: DD Destroy()s every pawn in those lists
    // once a battle nobody from the player is watching ends, which would
    // permanently delete the colonist. Left out of them, they're simply passed
    // back to the world when the arena map closes, like any other pawn.
    public static class PF_SettlementPresence
    {
        public static void PlaceResidents(Map map, Settlement settlement, bool notifyPlayer)
        {
            if (map == null || settlement == null || settlement.Faction == null || settlement.Faction.IsPlayer)
            {
                return;
            }

            PF_WandererTracker tracker = PF_WandererTracker.Instance;
            if (tracker == null)
            {
                return;
            }

            List<Pawn> residents = tracker.SettledResidentsOf(settlement);
            foreach (Pawn pawn in residents)
            {
                Pawn anchor = FindAnchor(map, settlement.Faction);
                IntVec3 near = anchor?.Position ?? map.Center;
                IntVec3 cell = CellFinder.RandomClosewalkCellNear(near, map, 5);

                Find.WorldPawns.RemovePawn(pawn);
                GenSpawn.Spawn(pawn, cell, map);

                Lord lord = anchor?.GetLord();
                lord?.AddPawn(pawn);

                PF_WandererDebugLog.LogEvent($"{pawn.LabelShort} placed on the map of their home {settlement.Label} (lord={lord?.LordJob?.GetType().Name ?? "none"}).");

                if (notifyPlayer)
                {
                    Messages.Message(
                        "PF_ResidentPresentMessage".Translate(pawn.LabelShort, settlement.Label),
                        pawn,
                        MessageTypeDefOf.NeutralEvent,
                        historical: false);
                }
            }
        }

        // A spawned humanlike pawn of the settlement's faction to stand next
        // to and share a lord with - preferring one that already has a lord
        // (the defending garrison) over stragglers without one.
        private static Pawn FindAnchor(Map map, Faction faction)
        {
            List<Pawn> candidates = map.mapPawns.SpawnedPawnsInFaction(faction)
                .Where((Pawn p) => p.RaceProps.Humanlike && !p.Dead && !p.Downed)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            List<Pawn> withLord = candidates.Where((Pawn p) => p.GetLord() != null).ToList();
            return withLord.Count > 0 ? withLord.RandomElement() : candidates.RandomElement();
        }
    }
}
