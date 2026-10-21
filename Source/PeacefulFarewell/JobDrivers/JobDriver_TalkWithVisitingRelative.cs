using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace PeacefulFarewell
{
    // A colonist walks over to a close relation/friend among friendly
    // visitors and talks with them for a while (see PF_VisitMeeting). The
    // outcome of the talk - warm, an invitation to come along, or being
    // snubbed - is rolled once when they arrive, played out as a few
    // interactions in the social log, and reported back to PF_GameComponent
    // when the talk ends. A talk that gets interrupted before anything was
    // said doesn't count.
    public class JobDriver_TalkWithVisitingRelative : JobDriver
    {
        private const int RepathIntervalTicks = 60;
        private const float TalkRangeSquared = 9f;
        private const float InteractRange = 6f;

        // An in-game hour - if the visitor keeps wandering out of reach the
        // colonist gives up and PF_GameComponent tries again later.
        private const int MaxApproachTicks = 2500;

        private const int LineIntervalTicks = 300;
        private const int LineRetryTicks = 60;
        private static readonly IntRange TalkDurationTicks = new IntRange(1000, 1500);

        // Index of the line on which the visitor invites the colonist along
        // (or snubs them) - a line or two of normal talk comes first.
        private const int InviteLineIndex = 2;
        private const int SnubLineIndex = 1;

        private int approachStartTick = -1;
        private int talkEndTick = -1;
        private int nextLineTick = -1;
        private int linesSaid;
        private bool inviteSaid;
        private bool snubSaid;
        private FarewellUtility.VisitTalkOutcome rolledOutcome;

        private Pawn Relative => job.targetA.Thing as Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => Relative == null || Relative.Dead || !Relative.Spawned || Relative.Map != pawn.Map || Relative.Downed || Relative.InMentalState);
            // Don't follow the visitors all the way to the map edge once
            // they've started leaving.
            this.FailOn(() =>
            {
                Lord lord = Relative?.GetLord();
                return lord != null && FarewellUtility.IsLordLeaving(lord);
            });

            Toil approach = new Toil();
            approach.defaultCompleteMode = ToilCompleteMode.Never;
            approach.initAction = delegate
            {
                if (approachStartTick < 0)
                {
                    approachStartTick = Find.TickManager.TicksGame;
                }
                pawn.pather.StartPath(Relative, PathEndMode.Touch);
            };
            approach.tickIntervalAction = delegate(int delta)
            {
                if (IsInTalkRange())
                {
                    ReadyForNextToil();
                    return;
                }
                if (Find.TickManager.TicksGame - approachStartTick > MaxApproachTicks)
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                if (!pawn.pather.Moving || GenTicks.IsTickIntervalDelta(pawn.thingIDNumber, RepathIntervalTicks, delta))
                {
                    pawn.pather.StartPath(Relative, PathEndMode.Touch);
                }
            };
            yield return approach;

            Toil talk = new Toil();
            talk.defaultCompleteMode = ToilCompleteMode.Never;
            talk.socialMode = RandomSocialMode.Off;
            talk.handlingFacing = true;
            talk.initAction = delegate
            {
                pawn.pather.StopDead();
                int now = Find.TickManager.TicksGame;
                if (talkEndTick < 0)
                {
                    talkEndTick = now + TalkDurationTicks.RandomInRange;
                    nextLineTick = now + LineRetryTicks;
                }
                if (rolledOutcome == FarewellUtility.VisitTalkOutcome.None)
                {
                    rolledOutcome = FarewellUtility.RollVisitTalkOutcome(pawn, Relative);
                }
                FarewellUtility.TryMakeVisitorListen(Relative, pawn);
            };
            talk.tickAction = delegate
            {
                if (Relative != null)
                {
                    pawn.rotationTracker.FaceTarget(Relative);
                }
            };
            talk.tickIntervalAction = delegate(int delta)
            {
                // Visitors don't always stand still - follow them a few
                // steps rather than talking across the map.
                if (!IsInTalkRange())
                {
                    if (!pawn.pather.Moving)
                    {
                        pawn.pather.StartPath(Relative, PathEndMode.Touch);
                    }
                }
                else if (pawn.pather.Moving)
                {
                    pawn.pather.StopDead();
                }

                int now = Find.TickManager.TicksGame;
                if (now >= nextLineTick)
                {
                    nextLineTick = now + (TrySayNextLine() ? LineIntervalTicks : LineRetryTicks);
                }

                if (snubSaid || now >= talkEndTick)
                {
                    FinishTalk();
                }
            };
            yield return talk;
        }

        private bool IsInTalkRange()
        {
            Pawn relative = Relative;
            return relative != null
                && (pawn.Position - relative.Position).LengthHorizontalSquared <= TalkRangeSquared
                && GenSight.LineOfSight(pawn.Position, relative.Position, pawn.Map, skipFirstCell: true);
        }

        // Plays out the rolled outcome: an exchange of normal lines, with the
        // visitor's invitation or snub landing on its own line. Returns false
        // when the line couldn't be said right now (vanilla's interaction
        // cooldown, out of range, ...) so it's retried shortly after.
        private bool TrySayNextLine()
        {
            Pawn relative = Relative;
            if (relative == null || !pawn.Position.InHorDistOf(relative.Position, InteractRange))
            {
                return false;
            }

            Pawn initiator;
            Pawn recipient;
            InteractionDef interaction;

            if (rolledOutcome == FarewellUtility.VisitTalkOutcome.Snub && linesSaid >= SnubLineIndex)
            {
                initiator = relative;
                recipient = pawn;
                interaction = PF_InteractionDefOf.PF_VisitTalk_Snub;
            }
            else if (rolledOutcome == FarewellUtility.VisitTalkOutcome.Invited && !inviteSaid && linesSaid >= InviteLineIndex)
            {
                initiator = relative;
                recipient = pawn;
                interaction = PF_InteractionDefOf.PF_VisitTalk_Invite;
            }
            else if (linesSaid % 2 == 0)
            {
                initiator = pawn;
                recipient = relative;
                interaction = Rand.Bool ? PF_InteractionDefOf.PF_VisitTalk_Reminisce : PF_InteractionDefOf.PF_VisitTalk_AskAboutLife;
            }
            else
            {
                initiator = relative;
                recipient = pawn;
                interaction = PF_InteractionDefOf.PF_VisitTalk_Stories;
            }

            if (initiator.interactions == null || !initiator.interactions.TryInteractWith(recipient, interaction))
            {
                return false;
            }

            linesSaid++;
            if (interaction == PF_InteractionDefOf.PF_VisitTalk_Invite)
            {
                inviteSaid = true;
            }
            else if (interaction == PF_InteractionDefOf.PF_VisitTalk_Snub)
            {
                snubSaid = true;
            }
            return true;
        }

        // Reports what actually happened rather than what was rolled - an
        // invitation that never got said (talk cut short) counts as a
        // normal warm talk, and a talk with no lines at all doesn't count.
        private void FinishTalk()
        {
            FarewellUtility.VisitTalkOutcome outcome;
            if (snubSaid)
            {
                outcome = FarewellUtility.VisitTalkOutcome.Snub;
            }
            else if (inviteSaid)
            {
                outcome = FarewellUtility.VisitTalkOutcome.Invited;
            }
            else if (linesSaid > 0)
            {
                outcome = FarewellUtility.VisitTalkOutcome.Warm;
            }
            else
            {
                outcome = FarewellUtility.VisitTalkOutcome.None;
            }

            Pawn relative = Relative;
            EndJobWith(JobCondition.Succeeded);
            PF_GameComponent.NotifyVisitTalkFinished(pawn, relative, outcome);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref approachStartTick, "approachStartTick", -1);
            Scribe_Values.Look(ref talkEndTick, "talkEndTick", -1);
            Scribe_Values.Look(ref nextLineTick, "nextLineTick", -1);
            Scribe_Values.Look(ref linesSaid, "linesSaid");
            Scribe_Values.Look(ref inviteSaid, "inviteSaid");
            Scribe_Values.Look(ref snubSaid, "snubSaid");
            Scribe_Values.Look(ref rolledOutcome, "rolledOutcome");
        }
    }
}
