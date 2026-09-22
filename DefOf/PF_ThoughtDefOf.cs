using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    [DefOf]
    public static class PF_ThoughtDefOf
    {
        public static ThoughtDef PF_RequestToLeaveDenied;
        public static ThoughtDef PF_LonelinessDenied;
        public static ThoughtDef PF_LonelinessExcitedToLeave;
        public static ThoughtDef PF_ColonistLeftRelief;
        public static ThoughtDef PF_ColonistLeftPity;
        public static ThoughtDef PF_ReunitedWithLovedOne;
        public static ThoughtDef PF_WillMissColonist;
        public static ThoughtDef PF_NotLikedHere;
        public static ThoughtDef PF_WanderlustDenied;
        public static ThoughtDef PF_WanderlustGreatJourney;
        public static ThoughtDef PF_WanderlustFamilyReturned;
        public static ThoughtDef PF_LetterFromAfar;

        static PF_ThoughtDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PF_ThoughtDefOf));
        }
    }
}
