using System.Collections.Generic;
using RimWorld;
using Verse;

namespace PeacefulFarewell
{
    // Some languages (Polish confirmed so far) need a different word ending
    // depending on pawn gender - English text doesn't ("they left" works for
    // everyone) - so DefInjected's single baked-in-at-load-time
    // label/description string can't hold every variant. Instead, gendered
    // languages provide extra Keyed strings per thought, and this resolves
    // the right one at grant/render time. Two independent gender axes can be
    // in play in the same thought's text:
    //   - "subject" gender: the pawn named via {0} (e.g. "odszedl/odeszla")
    //   - "owner" gender: the pawn who HOLDS the thought, when the text
    //     speaks in the first person about them (e.g. "chcialem/chcialam")
    // A thought can need either, both, or neither axis. Languages that don't
    // define these keys (English included) fall back to the def's normal
    // stage label/description untouched - resolve methods return null when
    // no gendered key exists, and callers decide the fallback.
    //
    // Thought.Description/LabelCap are read very often (Needs tab tooltips
    // redraw constantly while open) and the pawn's gender never changes
    // mid-game, so whether a given gendered key exists is cached per key
    // after the first lookup - avoids a Translator.CanTranslate string
    // lookup on every single UI redraw.
    public static class PF_GenderText
    {
        private static readonly Dictionary<string, string> ResolvedCache = new Dictionary<string, string>();

        // Owner-only axis: thoughts with no {0}, where the gendered word
        // refers to the pawn holding the thought (Thought_MemoryWithOwnerGender).
        public static string ResolveLabel(ThoughtDef def, Pawn owner)
        {
            return ResolveCached(def.defName + "_" + GenderSuffix(owner) + "_label");
        }

        public static string ResolveDescription(ThoughtDef def, Pawn owner)
        {
            return ResolveCached(def.defName + "_" + GenderSuffix(owner) + "_desc");
        }

        // Subject-only axis: thoughts with {0} naming someone other than the
        // holder, no first-person gendered phrasing about the holder
        // (Thought_MemoryWithSubject, most Thought_MemorySocialWithGender).
        public static string ResolveLabelForSubject(ThoughtDef def, Pawn subject)
        {
            return ResolveCached(def.defName + "_Subj" + GenderSuffix(subject) + "_label");
        }

        public static string ResolveDescriptionForSubject(ThoughtDef def, Pawn subject)
        {
            return ResolveCached(def.defName + "_Subj" + GenderSuffix(subject) + "_desc");
        }

        // Both axes at once: a handful of "Heard they're safe"/"No word"
        // variants have both a {0} subject AND first-person owner phrasing
        // in the same description (e.g. "Slyszalem/slyszalam, ze {0}
        // znalazl(a)..."). Falls back to the subject-only key (still
        // correct, just not owner-tailored) before falling back to plain def
        // text, so adding an owner axis to one variant doesn't require
        // immediately doing so for every sibling variant.
        public static string ResolveDescriptionForBoth(ThoughtDef def, Pawn subject, Pawn owner)
        {
            string both = ResolveCached(def.defName + "_Subj" + GenderSuffix(subject) + "_Owner" + GenderSuffix(owner) + "_desc");
            return both ?? ResolveDescriptionForSubject(def, subject);
        }

        // Null is a valid cached outcome (key doesn't exist for this
        // language), so a separate ContainsKey check is used instead of
        // TryGetValue-and-treat-null-as-miss.
        private static string ResolveCached(string key)
        {
            if (ResolvedCache.ContainsKey(key))
            {
                return ResolvedCache[key];
            }

            string resolved = Translator.CanTranslate(key) ? key.Translate().Resolve() : null;
            ResolvedCache[key] = resolved;
            return resolved;
        }

        private static string GenderSuffix(Pawn pawn)
        {
            return pawn != null && pawn.gender == Gender.Female ? "Female" : "Male";
        }
    }
}
