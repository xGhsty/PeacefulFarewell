using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    [DefOf]
    public static class PF_LetterDefOf
    {
        public static LetterDef PF_JoinRequest;
        public static LetterDef PF_LonelinessRequest;
        public static LetterDef PF_WanderlustRequest;

        static PF_LetterDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PF_LetterDefOf));
        }
    }
}
