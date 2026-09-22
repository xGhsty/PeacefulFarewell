using System;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace PeacefulFarewell
{
    public class PF_Settings : ModSettings
    {
        public float requestChancePerVisit = 0.07f;
        public float minRelationImportance = 20f;
        public bool debugMode = false;
        public bool customFileLoggingEnabled = false;

        public float lonelinessChancePerCheck = 0.03f;
        public float lonelinessAvgOpinionThreshold = -20f;
        public float lonelinessMaxSingleOpinionThreshold = 15f;
        public float lonelinessMinDaysInColony = 60f;
        public float wandererMinCheckDays = 7f;
        public float wandererMaxCheckDays = 30f;
        public bool wandererJoinedFactionLetterEnabled = true;

        public bool wanderlustEnabled = true;
        public float wanderlustChancePerCheck = 0.005f;

        public bool letterFromAfarEnabled = true;

        private Vector2 scrollPosition = Vector2.zero;
        private float viewHeight = 1450f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref requestChancePerVisit, "requestChancePerVisit", 0.07f);
            Scribe_Values.Look(ref minRelationImportance, "minRelationImportance", 20f);
            Scribe_Values.Look(ref debugMode, "debugMode", false);
            Scribe_Values.Look(ref customFileLoggingEnabled, "customFileLoggingEnabled", false);
            Scribe_Values.Look(ref lonelinessChancePerCheck, "lonelinessChancePerCheck", 0.03f);
            Scribe_Values.Look(ref lonelinessAvgOpinionThreshold, "lonelinessAvgOpinionThreshold", -20f);
            Scribe_Values.Look(ref lonelinessMaxSingleOpinionThreshold, "lonelinessMaxSingleOpinionThreshold", 15f);
            Scribe_Values.Look(ref lonelinessMinDaysInColony, "lonelinessMinDaysInColony", 60f);
            Scribe_Values.Look(ref wandererMinCheckDays, "wandererMinCheckDays", 7f);
            Scribe_Values.Look(ref wandererMaxCheckDays, "wandererMaxCheckDays", 30f);
            Scribe_Values.Look(ref wandererJoinedFactionLetterEnabled, "wandererJoinedFactionLetterEnabled", true);
            Scribe_Values.Look(ref wanderlustEnabled, "wanderlustEnabled", true);
            Scribe_Values.Look(ref wanderlustChancePerCheck, "wanderlustChancePerCheck", 0.005f);
            Scribe_Values.Look(ref letterFromAfarEnabled, "letterFromAfarEnabled", true);
        }

        public void DoWindowContents(Rect inRect)
        {
            Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            Text.Font = GameFont.Medium;
            listing.Label("PF_Setting_Section_VisitJoin".Translate());
            Text.Font = GameFont.Small;

            listing.Label("PF_Setting_ChanceLabel".Translate(requestChancePerVisit.ToStringPercent()));
            requestChancePerVisit = listing.Slider(requestChancePerVisit, 0f, 1f);

            listing.Gap();

            listing.Label("PF_Setting_MinImportanceLabel".Translate(minRelationImportance.ToString("F0")));
            minRelationImportance = listing.Slider(minRelationImportance, 0f, 100f);

            listing.GapLine();

            Text.Font = GameFont.Medium;
            listing.Label("PF_Setting_Section_Loneliness".Translate());
            Text.Font = GameFont.Small;

            listing.Label("PF_Setting_LonelinessChanceLabel".Translate(lonelinessChancePerCheck.ToStringPercent()));
            lonelinessChancePerCheck = listing.Slider(lonelinessChancePerCheck, 0f, 1f);

            listing.Gap();

            listing.Label("PF_Setting_LonelinessAvgOpinionLabel".Translate(lonelinessAvgOpinionThreshold.ToString("F0")));
            lonelinessAvgOpinionThreshold = listing.Slider(lonelinessAvgOpinionThreshold, -100f, 100f);

            listing.Gap();

            listing.Label("PF_Setting_LonelinessMaxSingleOpinionLabel".Translate(lonelinessMaxSingleOpinionThreshold.ToString("F0")));
            lonelinessMaxSingleOpinionThreshold = listing.Slider(lonelinessMaxSingleOpinionThreshold, -100f, 100f);

            listing.Gap();

            listing.Label("PF_Setting_LonelinessMinDaysInColonyLabel".Translate(lonelinessMinDaysInColony.ToString("F0")));
            lonelinessMinDaysInColony = listing.Slider(lonelinessMinDaysInColony, 0f, 180f);

            listing.GapLine();

            Text.Font = GameFont.Medium;
            listing.Label("PF_Setting_Section_Wanderer".Translate());
            Text.Font = GameFont.Small;

            listing.Label("PF_Setting_WandererMinCheckDaysLabel".Translate(wandererMinCheckDays.ToString("F0")));
            wandererMinCheckDays = listing.Slider(wandererMinCheckDays, 1f, 60f);
            if (wandererMinCheckDays > wandererMaxCheckDays)
            {
                wandererMaxCheckDays = wandererMinCheckDays;
            }

            listing.Gap();

            listing.Label("PF_Setting_WandererMaxCheckDaysLabel".Translate(wandererMaxCheckDays.ToString("F0")));
            wandererMaxCheckDays = listing.Slider(wandererMaxCheckDays, 1f, 60f);
            if (wandererMaxCheckDays < wandererMinCheckDays)
            {
                wandererMinCheckDays = wandererMaxCheckDays;
            }

            listing.Gap();

            listing.CheckboxLabeled("PF_Setting_WandererJoinedFactionLetterLabel".Translate(), ref wandererJoinedFactionLetterEnabled, "PF_Setting_WandererJoinedFactionLetterTooltip".Translate());

            listing.GapLine();

            Text.Font = GameFont.Medium;
            listing.Label("PF_Setting_Section_Wanderlust".Translate());
            Text.Font = GameFont.Small;

            listing.CheckboxLabeled("PF_Setting_WanderlustEnabledLabel".Translate(), ref wanderlustEnabled, "PF_Setting_WanderlustEnabledTooltip".Translate());

            listing.Gap();

            if (wanderlustEnabled)
            {
                listing.Label("PF_Setting_WanderlustChanceLabel".Translate(wanderlustChancePerCheck.ToStringPercent()));
                wanderlustChancePerCheck = listing.Slider(wanderlustChancePerCheck, 0f, 0.1f);
            }

            listing.GapLine();

            Text.Font = GameFont.Medium;
            listing.Label("PF_Setting_Section_LetterFromAfar".Translate());
            Text.Font = GameFont.Small;

            listing.CheckboxLabeled("PF_Setting_LetterFromAfarEnabledLabel".Translate(), ref letterFromAfarEnabled, "PF_Setting_LetterFromAfarEnabledTooltip".Translate());

            listing.GapLine();

            Text.Font = GameFont.Medium;
            listing.Label("PF_Setting_Section_Debug".Translate());
            Text.Font = GameFont.Small;

            listing.CheckboxLabeled("PF_Setting_DebugModeLabel".Translate(), ref debugMode, "PF_Setting_DebugModeTooltip".Translate());

            listing.Gap();

            listing.CheckboxLabeled("PF_Setting_CustomFileLoggingLabel".Translate(), ref customFileLoggingEnabled, "PF_Setting_CustomFileLoggingTooltip".Translate());

            listing.Gap();

            if (listing.ButtonText("PF_Setting_OpenLogFolderButton".Translate()) && !string.IsNullOrEmpty(PeacefulFarewellMod.ModRootDir))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + PeacefulFarewellMod.ModRootDir + "\"");
                }
                catch (Exception ex)
                {
                    PF_Log.Warning("Failed to open mod folder: " + ex);
                }
            }

            listing.Gap();

            if (listing.ButtonText("PF_Setting_GenerateReportButton".Translate()))
            {
                string path = PF_ReportGenerator.Generate();
                if (path != null)
                {
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    }
                    catch (Exception ex)
                    {
                        PF_Log.Warning("Failed to open folder for generated report: " + ex);
                    }
                    Messages.Message("PF_Setting_ReportGeneratedMessage".Translate(Path.GetFileName(path)), MessageTypeDefOf.TaskCompletion, historical: false);
                }
                else
                {
                    Messages.Message("PF_Setting_ReportFailedMessage".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }
            }

            listing.Gap();

            if (listing.ButtonText("PF_Setting_ViewWanderersButton".Translate()))
            {
                if (Current.Game == null)
                {
                    Messages.Message("PF_Setting_ViewWanderersNoGameMessage".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                }
                else
                {
                    Find.WindowStack.Add(new Dialog_PF_WandererOverview());
                }
            }

            viewHeight = listing.CurHeight;
            listing.End();
            Widgets.EndScrollView();
        }
    }
}
