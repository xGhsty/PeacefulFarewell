using RimWorld;

namespace PeacefulFarewell
{
    [DefOf]
    public static class PF_InteractionDefOf
    {
        public static InteractionDef PF_VisitTalk_Reminisce;
        public static InteractionDef PF_VisitTalk_AskAboutLife;
        public static InteractionDef PF_VisitTalk_Stories;
        public static InteractionDef PF_VisitTalk_Invite;
        public static InteractionDef PF_VisitTalk_Snub;

        static PF_InteractionDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PF_InteractionDefOf));
        }
    }
}
