using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // Saved work priorities and timetable for a colonist who left the colony,
    // so they can be restored if that colonist comes back later instead of
    // being reset to vanilla defaults. See PF_GameComponent.SaveWorkAndSchedule
    // / RestoreWorkAndSchedule.
    public class PF_WorkAndScheduleSnapshot : IExposable
    {
        public Dictionary<WorkTypeDef, int> workPriorities = new Dictionary<WorkTypeDef, int>();
        public List<TimeAssignmentDef> timetable = new List<TimeAssignmentDef>();
        public ApparelPolicy apparelPolicy;

        public void ExposeData()
        {
            Scribe_Collections.Look(ref workPriorities, "workPriorities", LookMode.Def, LookMode.Value);
            Scribe_Collections.Look(ref timetable, "timetable", LookMode.Def);
            Scribe_References.Look(ref apparelPolicy, "apparelPolicy");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (workPriorities == null)
                {
                    workPriorities = new Dictionary<WorkTypeDef, int>();
                }
                if (timetable == null)
                {
                    timetable = new List<TimeAssignmentDef>();
                }
            }
        }
    }
}
