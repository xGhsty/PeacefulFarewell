using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // A plain Thought_Memory (shows in the Needs tab, unlike Thought_MemorySocial)
    // that still needs to name a specific pawn in its label/description. Base
    // Thought_Memory has no otherPawn field and never substitutes {0} - only
    // Thought_MemorySocial does that, and vanilla's own equivalent (e.g.
    // Thought_TameVeneratedAnimalDied) solves this the same way: a small
    // Thought_Memory subclass carrying its own label string and overriding
    // LabelCap/Description to format it in.
    public class Thought_MemoryWithSubject : Thought_Memory
    {
        public string subjectLabel;
        public Pawn subject;

        public override string LabelCap
        {
            get
            {
                if (subjectLabel.NullOrEmpty())
                {
                    return base.LabelCap;
                }
                string gendered = PF_GenderText.ResolveLabelForSubject(def, subject);
                string text = gendered ?? CurStage.label;
                return text.Formatted(subjectLabel).CapitalizeFirst();
            }
        }

        public override string Description
        {
            get
            {
                if (subjectLabel.NullOrEmpty())
                {
                    return base.Description;
                }
                string gendered = PF_GenderText.ResolveDescriptionForSubject(def, subject);
                string text = gendered ?? CurStage.description;
                return text.Formatted(subjectLabel);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref subjectLabel, "subjectLabel");
            Scribe_References.Look(ref subject, "subject");
        }
    }
}
