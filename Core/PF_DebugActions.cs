using LudeonTK;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    public static class PF_DebugActions
    {
        private const string Category = "Peaceful Farewell";

        [DebugAction(Category, "Force visitor join request", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceVisitorRequest()
        {
            if (!PF_GameComponent.TryForceVisitorRequest(Find.CurrentMap))
            {
                Messages.Message("No eligible visitor/colonist pair found on this map.", MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        [DebugAction(Category, "Force wanderlust request", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceWanderlustRequest()
        {
            if (!PF_GameComponent.TryForceWanderlustRequest(Find.CurrentMap))
            {
                Messages.Message("No eligible teenage colonist found on this map.", MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        [DebugAction(Category, "Force loneliness request", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceLonelinessRequest()
        {
            if (!PF_GameComponent.TryForceLonelinessRequest(Find.CurrentMap))
            {
                Messages.Message("No eligible adult colonist found on this map.", MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        [DebugAction(Category, "View wanderer overview", allowedGameStates = AllowedGameStates.Playing)]
        private static void ViewWandererOverview()
        {
            Find.WindowStack.Add(new Dialog_PF_WandererOverview());
        }

        // Rescue for pawns spawned into solid rock by the pre-fix
        // CellFinder.RandomEdgeCell bug in PF_WandererTracker.ReturnToColony -
        // teleports the selected pawn to the nearest walkable, reachable spot.
        [DebugAction(Category, "Rescue selected pawn from rock", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RescueSelectedPawnFromRock()
        {
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn == null || pawn.Map == null)
            {
                Messages.Message("Select a spawned pawn first.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            Map map = pawn.Map;
            if (!CellFinder.TryFindRandomCellNear(
                pawn.Position,
                map,
                50,
                c => c.Standable(map) && c.Walkable(map) && !c.Fogged(map) && map.reachability.CanReachColony(c),
                out IntVec3 cell))
            {
                Messages.Message("Could not find a safe cell nearby.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }

            pawn.Position = cell;
            pawn.Notify_Teleported();
            Messages.Message($"Teleported {pawn.LabelShort} to a safe cell.", MessageTypeDefOf.PositiveEvent, historical: false);
        }
    }
}
