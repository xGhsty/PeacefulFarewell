using Verse;

namespace PeacefulFarewell
{
    // RimWorld's Translate() does not randomize between duplicate <Key> entries
    // in Keyed XML at runtime - it always resolves to the same one. Variant
    // text keys are stored as "{baseKey}0", "{baseKey}1", ... instead, and this
    // picks one at random each time.
    public static class PF_Text
    {
        public static TaggedString Variant(string baseKey, int variantCount, params NamedArgument[] args)
        {
            string key = baseKey + Rand.Range(0, variantCount);
            return key.Translate(args);
        }
    }
}
