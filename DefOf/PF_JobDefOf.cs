using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    [DefOf]
    public static class PF_JobDefOf
    {
        public static JobDef PF_JoinVisitingFaction;
        public static JobDef PF_LeaveOutOfLoneliness;
        public static JobDef PF_LeaveOutOfWanderlust;
        public static JobDef PF_FollowDepartingOwner;

        static PF_JobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PF_JobDefOf));
        }
    }
}
