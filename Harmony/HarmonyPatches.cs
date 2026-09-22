using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PeacefulFarewell
{
    // While a colonist is running our own "leaving the colony" jobs, the player
    // should not be able to draft them, queue orders, or manage their gear -
    // they are effectively already gone. IsColonistPlayerControlled is the same
    // switch vanilla uses to gate the Gear tab, draft gizmos and float menu
    // orders, so forcing it false for the duration of our jobs covers all of
    // those at once.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.IsColonistPlayerControlled), MethodType.Getter)]
    public static class Patch_IsColonistPlayerControlled
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            if (FarewellUtility.IsRunningFarewellJob(__instance))
            {
                __result = false;
            }
        }
    }

    // IsColonistPlayerControlled only gates vanilla's own gizmos (draft, gear,
    // work tab, etc). Modded gizmos - e.g. an item ability button added by a
    // CompProperties on equipped gear - come from Pawn.GetGizmos() and don't
    // check that flag, so a colonist mid-farewell-job could still be ordered
    // around through them. Strip every gizmo that isn't from the base game
    // assembly while a farewell job is running so those can't interrupt or
    // fight the departure. Assembly whitelist (not namespace) because mods
    // are free to patch/extend types under the RimWorld/Verse namespaces
    // themselves.
    //
    // HarmonyPriority.Last is required here: other mods add their own gizmos
    // via their own GetGizmos postfix (e.g. Hauler's Dream's "Unload Now"
    // button), and Harmony runs same-priority postfixes in load order, not
    // patch-declaration order. Without forcing this postfix to run dead last,
    // a mod whose postfix happens to run after this one re-appends its gizmo
    // to __result and the filter above never sees it - confirmed via
    // decompiling HaulersDream.dll's own unpriorotized Patch_Pawn_GetGizmos,
    // whose "Unload Now" action force-starts an unload job that preempts
    // whatever the pawn is currently doing, interrupting a farewell job.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    [HarmonyPriority(Priority.Last)]
    public static class Patch_Pawn_GetGizmos
    {
        private static readonly Assembly VanillaAssembly = typeof(Pawn).Assembly;

        public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!FarewellUtility.IsRunningFarewellJob(__instance))
            {
                return;
            }

            __result = __result.Where((Gizmo g) => g.GetType().Assembly == VanillaAssembly);
        }
    }

    // CaravanFormingUtility.AllSendablePawns (the list Dialog_FormCaravan draws
    // its selectable-pawn rows from, for both forming a new caravan and
    // reforming one) never checks IsColonistPlayerControlled - it only cares
    // about faction/downed/mental-state/lord. That let a colonist mid-farewell
    // job (still spawned, still player-faction, no lord of their own) be added
    // to a caravan and then removed again, which interrupted their departure
    // job partway through and left them stuck. Strip farewell-job pawns out of
    // the sendable list entirely so they can't be selected in the first place.
    [HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.AllSendablePawns))]
    public static class Patch_AllSendablePawns
    {
        public static void Postfix(ref List<Pawn> __result)
        {
            __result.RemoveAll((Pawn p) => FarewellUtility.IsRunningFarewellJob(p));
        }
    }

    // A prisoner who just got recruited hasn't had time to build up real
    // opinions with the rest of the colony - a loneliness scan running right
    // after would judge them on that blank slate and could flag them as
    // "not liked here" within the hour. DoRecruit is vanilla's single choke
    // point for a successful recruitment (interaction-based or otherwise),
    // so shield the pawn from the loneliness check from here rather than
    // hooking every possible recruitment path individually.
    //
    // DoRecruit has two overloads; the 3-arg one is a thin wrapper (14 bytes
    // of IL, confirmed via reflection) that just calls the 5-arg one, which
    // holds the real implementation - so only the 5-arg one needs patching.
    // It takes two `ref` (out-style) string params, which an attribute can't
    // express (must be a compile-time constant), so this is patched manually
    // in PeacefulFarewellMod's constructor instead of via [HarmonyPatch].
    public static class Patch_DoRecruit
    {
        public static void Postfix(Pawn recruitee)
        {
            PF_GameComponent.NotifyRecruited(recruitee);
        }
    }

    // While a pawn's fate is still unresolved out in the world (tracked by
    // PF_WandererTracker - they've left the colony but haven't returned,
    // joined a faction, or been lost yet), show "Missing" in the relations tab
    // instead of vanilla's faction/situation label, which would otherwise read
    // oddly for a factionless world pawn. Purely cosmetic - doesn't touch
    // Faction, opinions, or the tracker itself.
    [HarmonyPatch(typeof(SocialCardUtility), nameof(SocialCardUtility.GetPawnSituationLabel))]
    public static class Patch_GetPawnSituationLabel
    {
        public static void Postfix(Pawn pawn, ref string __result)
        {
            if (Current.Game?.World == null)
            {
                return;
            }

            if (Current.Game.World.GetComponent<PF_WandererTracker>()?.IsTracked(pawn) == true)
            {
                __result = "PF_MissingSituationLabel".Translate(pawn.Named("PAWN"));
            }
        }
    }

    // Vanilla's world-pawn "redress" system can reuse an existing factionless
    // world pawn to fill a generation request instead of always generating a
    // brand-new one (see PawnGenerator.IsValidCandidateToRedress/RedressPawn).
    // Confirmed via IL inspection: this normally requires pawn.Faction to
    // match the request's faction, but callers can set
    // PawnGenerationRequest.WorldPawnFactionDoesntMatter to skip that check
    // entirely - which means our tracked wanderers (Faction == null for the
    // whole min-max wait window) are visible, eligible candidates for ANY
    // such request, including ones for a totally unrelated faction. That's
    // what silently reassigned a Wanderlust pawn's Faction within minutes,
    // completely bypassing wandererMinCheckDays. There's no pawn-side opt-out
    // field in vanilla, so this prefix rejects redress outright for any pawn
    // PF_WandererTracker still considers unresolved - their fate must go
    // through the tracker's own roll, not get pre-empted by an unrelated
    // vanilla pawn-generation request picking them out of the world pawn pool.
    public static class Patch_IsValidCandidateToRedress
    {
        public static bool Prefix(Pawn pawn, ref bool __result)
        {
            if (Current.Game?.World == null)
            {
                return true;
            }

            if (Current.Game.World.GetComponent<PF_WandererTracker>()?.IsTracked(pawn) == true)
            {
                if (PeacefulFarewellMod.Settings.debugMode)
                {
                    PF_Log.Message($"Blocked vanilla redress of tracked wanderer {pawn.LabelShort} - fate still pending in PF_WandererTracker.");
                }
                __result = false;
                return false;
            }

            return true;
        }
    }
}
