using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace PeacefulFarewell
{
    // Live in-game view of everyone PF_WandererTracker currently has out in
    // the world - both unresolved wanderers and pawns who've already settled
    // with a friendly faction. Reads straight from the tracker each time it's
    // opened, so unlike WandererDebugLog.txt it never goes stale and needs no
    // debugMode setting to be useful.
    //
    // Laid out as a card grid (portrait + faction icon + colored status)
    // rather than plain text rows, since these pawns have no map presence of
    // their own to look at otherwise - the card is the only "face" the player
    // gets for someone who's out in the world.
    public class Dialog_PF_WandererOverview : Window
    {
        private Vector2 scrollPosition = Vector2.zero;

        private const float CardWidth = 320f;
        private const float CardHeight = 96f;
        private const float CardGap = 8f;
        private const float PortraitSize = 56f;

        private static readonly Color ColorForceDecision = new Color(0.95f, 0.75f, 0.25f);
        private static readonly Color ColorSettled = new Color(0.45f, 0.85f, 0.45f);
        private static readonly Color ColorWaiting = new Color(0.7f, 0.7f, 0.7f);

        public override Vector2 InitialSize => new Vector2(640f, 660f);

        public Dialog_PF_WandererOverview()
        {
            doCloseButton = true;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            PF_WandererTracker tracker = Current.Game?.World?.GetComponent<PF_WandererTracker>();
            if (tracker == null)
            {
                Widgets.Label(inRect, "PF_Overview_NoWorld".Translate());
                return;
            }

            List<PF_WandererTracker.WandererOverviewEntry> wandering = tracker.GetOverviewEntries();
            List<PF_WandererTracker.SettledOverviewEntry> settled = tracker.GetSettledOverviewEntries();

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 35f), "PF_Overview_Title".Translate());
            Text.Font = GameFont.Small;

            Rect outerRect = new Rect(0f, 40f, inRect.width, inRect.height - 40f - (doCloseButton ? 40f : 0f));
            float viewWidth = outerRect.width - 16f;
            int columns = System.Math.Max(1, Mathf.FloorToInt((viewWidth + CardGap) / (CardWidth + CardGap)));

            const float sectionHeaderHeight = 30f;
            const float noneRowHeight = 22f;

            float viewHeight = 30f + sectionHeaderHeight + 10f + sectionHeaderHeight + 10f;
            viewHeight += wandering.Count == 0 ? noneRowHeight : GridHeight(wandering.Count, columns);
            viewHeight += settled.Count == 0 ? noneRowHeight : GridHeight(settled.Count, columns);

            Rect viewRect = new Rect(0f, 0f, viewWidth, viewHeight);
            Widgets.BeginScrollView(outerRect, ref scrollPosition, viewRect);

            float y = 0f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, viewRect.width, sectionHeaderHeight), "PF_Overview_Section_Wandering".Translate(wandering.Count));
            Text.Font = GameFont.Small;
            y += sectionHeaderHeight;

            if (wandering.Count == 0)
            {
                Widgets.Label(new Rect(0f, y, viewRect.width, noneRowHeight), "PF_Overview_None".Translate());
                y += noneRowHeight;
            }
            else
            {
                y = DrawWanderingGrid(wandering, viewRect.width, columns, y);
            }

            y += 10f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, viewRect.width, sectionHeaderHeight), "PF_Overview_Section_Settled".Translate(settled.Count));
            Text.Font = GameFont.Small;
            y += sectionHeaderHeight;

            if (settled.Count == 0)
            {
                Widgets.Label(new Rect(0f, y, viewRect.width, noneRowHeight), "PF_Overview_None".Translate());
                y += noneRowHeight;
            }
            else
            {
                y = DrawSettledGrid(settled, viewRect.width, columns, y);
            }

            Widgets.EndScrollView();
        }

        private static float GridHeight(int count, int columns)
        {
            int rows = Mathf.CeilToInt((float)count / columns);
            return rows * (CardHeight + CardGap);
        }

        private float DrawWanderingGrid(List<PF_WandererTracker.WandererOverviewEntry> entries, float width, int columns, float y)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Rect cardRect = CardRect(i, columns, width, y);
                DrawWanderingCard(cardRect, entries[i]);
            }
            return y + GridHeight(entries.Count, columns);
        }

        private float DrawSettledGrid(List<PF_WandererTracker.SettledOverviewEntry> entries, float width, int columns, float y)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Rect cardRect = CardRect(i, columns, width, y);
                DrawSettledCard(cardRect, entries[i]);
            }
            return y + GridHeight(entries.Count, columns);
        }

        private static Rect CardRect(int index, int columns, float totalWidth, float gridTop)
        {
            int col = index % columns;
            int row = index / columns;
            // Spread any leftover width evenly between columns instead of
            // leaving it as a single dead gap on the right edge.
            float colStride = (totalWidth - CardWidth) / System.Math.Max(1, columns - 1);
            float x = columns == 1 ? 0f : col * colStride;
            float y = gridTop + row * (CardHeight + CardGap);
            return new Rect(x, y, CardWidth, CardHeight);
        }

        private void DrawWanderingCard(Rect rect, PF_WandererTracker.WandererOverviewEntry entry)
        {
            Pawn pawn = entry.Pawn;
            Color statusColor = entry.ForceDecisionNext ? ColorForceDecision : ColorWaiting;
            string statusText = entry.ForceDecisionNext
                ? "PF_Overview_ForceDecision".Translate()
                : "PF_Overview_ResolveChance".Translate(entry.ResolveChancePercent.ToString("F0"));
            string subText = "PF_Overview_WanderingCardSub".Translate(
                entry.Reason.ToString(),
                entry.DaysTracked.ToString("F1"));
            string departureText = "PF_Overview_DepartedOn".Translate(DepartureDateString(entry.DepartureTick, entry.DepartureTile));

            DrawCardFrame(rect, pawn, statusColor, statusText, subText, departureText);
        }

        private void DrawSettledCard(Rect rect, PF_WandererTracker.SettledOverviewEntry entry)
        {
            Pawn pawn = entry.Pawn;
            string statusText = "PF_Overview_NextLetterInDays".Translate(entry.DaysUntilNextLetterCheck.ToString("F1"));
            string subText = pawn.Faction?.Name ?? "PF_Overview_NoFaction".Translate();
            string departureText = "PF_Overview_DepartedOn".Translate(DepartureDateString(entry.DepartureTick, entry.DepartureTile));

            DrawCardFrame(rect, pawn, ColorSettled, statusText, subText, departureText);
        }

        // Full calendar date (day/quadrum/year) the pawn originally left the
        // colony, anchored to the given tile since RimWorld's date readout is
        // longitude-dependent. Falls back to a tile-less relative "days ago"
        // string if no tile could be resolved at all (e.g. no world objects
        // exist yet), which should only happen in degenerate edge cases.
        private static string DepartureDateString(int departureTick, int tile)
        {
            if (tile >= 0)
            {
                return GenDate.DateFullStringAt(departureTick, Find.WorldGrid.LongLatOf(tile));
            }
            float daysAgo = (float)(Find.TickManager.TicksGame - departureTick) / GenDate.TicksPerDay;
            return "PF_Overview_DaysAgoFallback".Translate(daysAgo.ToString("F1"));
        }

        private void DrawCardFrame(Rect rect, Pawn pawn, Color statusColor, string statusText, string subText, string departureText)
        {
            Widgets.DrawBoxSolid(rect, new Color(1f, 1f, 1f, 0.03f));
            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }
            Widgets.DrawBox(rect);

            Rect portraitRect = new Rect(rect.x + 6f, rect.y + (rect.height - PortraitSize) / 2f, PortraitSize, PortraitSize);
            DrawPortrait(portraitRect, pawn);

            Faction faction = pawn.Faction;
            // Reserve space for the faction icon/jump button in the top-right
            // corner so long translated status strings (Polish especially runs
            // much longer than English here) wrap instead of drawing under them.
            float iconReserve = faction != null ? 28f : 0f;

            float textX = portraitRect.xMax + 8f;
            float textWidth = rect.xMax - textX - 6f - iconReserve;

            Rect nameRect = new Rect(textX, rect.y + 4f, textWidth, 18f);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            Widgets.Label(nameRect, pawn.LabelShort.Truncate(textWidth));

            Rect subRect = new Rect(textX, nameRect.yMax, textWidth, 16f);
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(subRect, subText.Truncate(textWidth));

            Rect departureRect = new Rect(textX, subRect.yMax, textWidth, 16f);
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(departureRect, departureText.Truncate(textWidth));

            Rect statusRect = new Rect(textX, departureRect.yMax, textWidth, 16f);
            GUI.color = statusColor;
            Text.Font = GameFont.Tiny;
            Widgets.Label(statusRect, statusText.Truncate(textWidth));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            if (faction != null)
            {
                Rect factionIconRect = new Rect(rect.xMax - 26f, rect.y + 4f, 20f, 20f);
                FactionUIUtility.DrawFactionIconWithTooltip(factionIconRect, faction);

                Settlement settlement = FindFactionSettlement(faction);
                if (settlement != null)
                {
                    Rect jumpRect = new Rect(rect.xMax - 26f, rect.yMax - 24f, 20f, 20f);
                    if (Widgets.ButtonImage(jumpRect, TexButton.Search))
                    {
                        JumpToSettlement(settlement);
                    }
                    TooltipHandler.TipRegion(jumpRect, "PF_Overview_JumpToSettlement".Translate(settlement.Label));
                }
            }

            if (Widgets.ButtonInvisible(rect))
            {
                Find.WindowStack.Add(new Dialog_InfoCard(pawn));
            }
        }

        private static void DrawPortrait(Rect rect, Pawn pawn)
        {
            RenderTexture portrait = PortraitsCache.Get(pawn, rect.size, Rot4.South, compensateForUIScale: true);
            GUI.DrawTexture(rect, portrait);
        }

        // Only settlements (permanent bases), not any other world object type,
        // count as a meaningful "jump to" target - and only the first one, since
        // a pawn only ever belongs to one faction at a time here.
        private static Settlement FindFactionSettlement(Faction faction)
        {
            List<Settlement> settlements = Find.WorldObjects?.Settlements;
            if (settlements == null)
            {
                return null;
            }
            return settlements.FirstOrDefault(s => s.Faction == faction);
        }

        private static void JumpToSettlement(Settlement settlement)
        {
            CameraJumper.TryJumpAndSelect(settlement);
        }
    }
}
