using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeacefulFarewell
{
    public class PeacefulFarewellMod : Mod
    {
        public static PF_Settings Settings;
        public static string ModRootDir;

        public PeacefulFarewellMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PF_Settings>();
            ModRootDir = content.RootDir;

            Harmony harmony = new Harmony("ghost.peacefulfarewell");
            harmony.PatchAll();

            // DoRecruit's 5-arg overload takes `ref` string params, which a
            // [HarmonyPatch] attribute can't target (attribute args must be
            // compile-time constants) - patched manually here instead. See
            // Patch_DoRecruit in HarmonyPatches.cs for why only this overload
            // needs patching.
            MethodInfo doRecruit = AccessTools.Method(
                typeof(InteractionWorker_RecruitAttempt),
                nameof(InteractionWorker_RecruitAttempt.DoRecruit),
                new Type[] { typeof(Pawn), typeof(Pawn), typeof(string).MakeByRefType(), typeof(string).MakeByRefType(), typeof(bool), typeof(bool) });
            harmony.Patch(doRecruit, postfix: new HarmonyMethod(typeof(Patch_DoRecruit), nameof(Patch_DoRecruit.Postfix)));

            // IsValidCandidateToRedress is private, and a [HarmonyPatch]
            // attribute can't easily target private static methods declared
            // this deep without risking an AmbiguousMatchException the way
            // DoRecruit had - patched manually here, same pattern as above.
            // See Patch_IsValidCandidateToRedress in HarmonyPatches.cs.
            MethodInfo isValidCandidateToRedress = AccessTools.Method(
                typeof(PawnGenerator),
                "IsValidCandidateToRedress",
                new Type[] { typeof(Pawn), typeof(PawnGenerationRequest) });
            harmony.Patch(isValidCandidateToRedress, prefix: new HarmonyMethod(typeof(Patch_IsValidCandidateToRedress), nameof(Patch_IsValidCandidateToRedress.Prefix)));

            PF_FileLog.WriteSessionHeaderIfNeeded();
            PF_Log.Message("Mod loaded.");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.DoWindowContents(inRect);
            base.DoSettingsWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "Peaceful Farewell";
        }
    }
}
