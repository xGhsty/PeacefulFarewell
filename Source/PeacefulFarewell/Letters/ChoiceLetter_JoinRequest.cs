using RimWorld;
using Verse;
using Verse.AI.Group;

namespace PeacefulFarewell
{
    public class ChoiceLetter_JoinRequest : ChoiceLetter_PF_Request
    {
        public Pawn relatedPawn;
        public Lord visitingLord;
        public Faction targetFaction;

        protected override void OnAccepted()
        {
            FarewellUtility.StartJoinJob(colonist, relatedPawn, visitingLord, targetFaction);
        }

        protected override void OnRejected()
        {
            FarewellUtility.SendDeniedThought(colonist);
            PF_GameComponent.NotifyJoinRequestDenied(colonist);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref relatedPawn, "relatedPawn");
            Scribe_References.Look(ref visitingLord, "visitingLord");
            Scribe_References.Look(ref targetFaction, "targetFaction");
        }
    }
}
