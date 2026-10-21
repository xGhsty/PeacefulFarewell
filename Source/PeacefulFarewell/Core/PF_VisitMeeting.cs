using RimWorld;
using Verse;
using Verse.AI.Group;

namespace PeacefulFarewell
{
    // One colonist meeting a close relation/friend who arrived with friendly
    // visitors. Replaces the old "roll requestChancePerVisit every hour and
    // send the join letter straight away": the colonist now goes over to
    // talk with the visitor a few times during the visit (see
    // JobDriver_TalkWithVisitingRelative), and only after the last of those
    // talks does PF_GameComponent decide - from how the talks went, the bond,
    // mood and ties in the colony - whether they ask to leave or stay.
    //
    // Kept in the list after the decision (concluded = true) until the
    // visitors leave, so the hourly candidate scan doesn't start a second
    // meeting for the same colonist during the same visit.
    public class PF_VisitMeeting : IExposable
    {
        public Pawn colonist;
        public Pawn relative;
        public Lord lord;
        public Faction faction;

        public int talksDone;
        public int nextTalkTick;
        public float talkPoints;
        public int snubs;
        public bool concluded;

        // Not saved. Set by the debug "Hurry visitor talks" action so the
        // next talk skips the soft checks in CanStartVisitTalk; lastBlockReason
        // keeps the debug log from repeating the same reason every check.
        public bool debugForceNextTalk;
        public string lastBlockReason;

        public void ExposeData()
        {
            Scribe_References.Look(ref colonist, "colonist");
            Scribe_References.Look(ref relative, "relative");
            Scribe_References.Look(ref lord, "lord");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref talksDone, "talksDone");
            Scribe_Values.Look(ref nextTalkTick, "nextTalkTick");
            Scribe_Values.Look(ref talkPoints, "talkPoints");
            Scribe_Values.Look(ref snubs, "snubs");
            Scribe_Values.Look(ref concluded, "concluded");
        }

        public override string ToString()
        {
            return $"{colonist?.LabelShort ?? "null"} <-> {relative?.LabelShort ?? "null"} ({faction?.Name ?? "null"}): talks={talksDone}, points={talkPoints}, snubs={snubs}, concluded={concluded}";
        }
    }
}
