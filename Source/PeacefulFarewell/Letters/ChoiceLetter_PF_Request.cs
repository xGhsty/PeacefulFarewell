using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace PeacefulFarewell
{
    // Shared accept/refuse letter for all of the mod's departure requests.
    // Subclasses only say what accepting and refusing actually do.
    public abstract class ChoiceLetter_PF_Request : ChoiceLetter
    {
        public Pawn colonist;
        public bool decided;

        protected abstract void OnAccepted();

        protected abstract void OnRejected();

        private bool ColonistUnavailable => colonist == null || !colonist.Spawned || colonist.Dead || colonist.Downed;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                DiaOption accept = new DiaOption("PF_AcceptButton".Translate())
                {
                    action = Accept,
                    resolveTree = true
                };

                DiaOption reject = new DiaOption("PF_RejectButton".Translate())
                {
                    action = Reject,
                    resolveTree = true
                };

                if (decided)
                {
                    accept.Disable("PF_AlreadyDecided".Translate());
                    reject.Disable("PF_AlreadyDecided".Translate());
                }
                else if (ColonistUnavailable)
                {
                    accept.Disable("PF_CannotAccept_ColonistUnavailable".Translate());
                }

                yield return accept;
                yield return reject;
                yield return Option_JumpToLocationNoClose;
                yield return ArchivedOnly ? Option_Close : Option_Postpone;
            }
        }

        private DiaOption Option_JumpToLocationNoClose
        {
            get
            {
                DiaOption diaOption = new DiaOption("JumpToLocation".Translate())
                {
                    action = () => CameraJumper.TryJumpAndSelect(lookTargets.TryGetPrimaryTarget()),
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
            if (!ColonistUnavailable)
            {
                OnAccepted();
            }
        }

        private void Reject()
        {
            decided = true;
            Find.LetterStack.RemoveLetter(this);
            if (colonist != null && !colonist.Dead)
            {
                OnRejected();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref colonist, "colonist");
            Scribe_Values.Look(ref decided, "decided");
        }
    }

    public class ChoiceLetter_LonelinessRequest : ChoiceLetter_PF_Request
    {
        protected override void OnAccepted()
        {
            FarewellUtility.StartLeaveOutOfLonelinessJob(colonist);
        }

        protected override void OnRejected()
        {
            FarewellUtility.SendLonelinessDeniedThought(colonist);
            PF_GameComponent.NotifyLonelinessRequestDenied(colonist);
        }
    }

    public class ChoiceLetter_WanderlustRequest : ChoiceLetter_PF_Request
    {
        protected override void OnAccepted()
        {
            FarewellUtility.StartLeaveOutOfWanderlustJob(colonist);
        }

        protected override void OnRejected()
        {
            FarewellUtility.SendWanderlustDeniedThought(colonist);
            PF_GameComponent.NotifyWanderlustRequestDenied(colonist);
        }
    }
}
