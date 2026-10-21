using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // Thought_MemorySocial already substitutes {0} with otherPawn's name via
    // TryGainMemory(def, otherPawn) - vanilla behavior, works correctly. What
    // it can't do is pick a different description depending on otherPawn's
    // gender (e.g. Polish "odszedl" vs "odeszla"), since the description
    // string is baked into the def once at DefInjected load time. This
    // subclass overrides Description/LabelCapSocial to check for a
    // gender-specific Keyed string first (see PF_GenderText), falling back to
    // the normal def text for languages that don't need the distinction.
    public class Thought_MemorySocialWithGender : Thought_MemorySocial
    {
        // Tries the combined subject+owner gender key first (a few variants,
        // e.g. HeardTheyreSafePartner0, speak in first person about the
        // holder too - "moglbym/moglabym" - alongside the {0} subject), then
        // falls back to subject-only, then to the plain def text. Returns the
        // raw gendered text (still containing a literal "{0}") when any
        // gendered key exists, or the already-fully-formatted vanilla string
        // as the ultimate fallback - so .Formatted only runs in the
        // gendered-key branch.
        public override string Description
        {
            get
            {
                string gendered = PF_GenderText.ResolveDescriptionForBoth(def, otherPawn, pawn);
                return gendered == null ? base.Description : gendered.Formatted(otherPawn.LabelShort);
            }
        }

        public override string LabelCapSocial
        {
            get
            {
                string gendered = PF_GenderText.ResolveLabelForSubject(def, otherPawn);
                return gendered == null ? base.LabelCapSocial : gendered.Formatted(otherPawn.LabelShort).CapitalizeFirst();
            }
        }
    }
}
