using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // For thoughts with no {0} at all, where the gendered word refers to the
    // pawn who HOLDS the thought, not someone named in it - e.g. Polish
    // "chcialem/chcialam odejsc" (I wanted to leave) on PF_LonelinessDenied.
    // Mirrors Thought_MemorySocialWithGender/Thought_MemoryWithSubject, but
    // resolves the gender key against `pawn` (inherited from base Thought)
    // instead of an otherPawn/subject field.
    public class Thought_MemoryWithOwnerGender : Thought_Memory
    {
        public override string LabelCap
        {
            get
            {
                string gendered = PF_GenderText.ResolveLabel(def, pawn);
                return gendered == null ? base.LabelCap : gendered.CapitalizeFirst();
            }
        }

        public override string Description
        {
            get
            {
                string gendered = PF_GenderText.ResolveDescription(def, pawn);
                return gendered ?? base.Description;
            }
        }
    }
}
