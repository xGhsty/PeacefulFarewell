using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PeacefulFarewell
{
    public class ChoiceLetter_LonelinessRequest : ChoiceLetter
    {
        public Pawn colonist;
        public bool decided;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                DiaOption accept = new DiaOption("PF_AcceptButton".Translate())
                {
                    action = delegate
                    {
                        Accept();
                    },
                    resolveTree = true
                };

                DiaOption reject = new DiaOption("PF_RejectButton".Translate())
                {
                    action = delegate
                    {
                        Reject();
                    },
                    resolveTree = true
                };

                if (decided)
                {
                    accept.Disable("PF_AlreadyDecided".Translate());
                    reject.Disable("PF_AlreadyDecided".Translate());
                }
                else if (colonist == null || !colonist.Spawned || colonist.Dead || colonist.Downed)
                {
                    accept.Disable("PF_CannotAccept_ColonistUnavailable".Translate());
                }

                yield return accept;
                yield return reject;
                yield return Option_JumpToLocationNoClose;
                if (ArchivedOnly)
                {
                    yield return Option_Close;
                }
                else
                {
                    yield return Option_Postpone;
                }
            }
        }

        private DiaOption Option_JumpToLocationNoClose
        {
            get
            {
                DiaOption diaOption = new DiaOption("JumpToLocation".Translate())
                {
                    action = delegate
                    {
                        CameraJumper.TryJumpAndSelect(lookTargets.TryGetPrimaryTarget());
                    },
                    resolveTree = true
                };
                if (!lookTargets.IsValid())
                {
                    diaOption.Disable(null);
                }
                return diaOption;
            }
        }

        private void Accept()
        {
            decided = true;
            Find.LetterStack.RemoveLetter(this);
            if (colonist == null || !colonist.Spawned || colonist.Dead || colonist.Downed)
            {
                return;
            }
            FarewellUtility.StartLeaveOutOfLonelinessJob(colonist);
        }

        private void Reject()
        {
            decided = true;
            Find.LetterStack.RemoveLetter(this);
            if (colonist != null && !colonist.Dead)
            {
                FarewellUtility.SendLonelinessDeniedThought(colonist);
                PF_GameComponent.NotifyLonelinessRequestDenied(colonist);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref colonist, "colonist");
            Scribe_Values.Look(ref decided, "decided");
        }
    }
}
