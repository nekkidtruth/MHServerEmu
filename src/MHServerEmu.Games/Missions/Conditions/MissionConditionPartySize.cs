using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Missions.Conditions
{
    public class MissionConditionPartySize : MissionPlayerCondition
    {
        private MissionConditionPartySizePrototype _proto;
        private Action<PartySizeChangedGameEvent> _partySizeChangedAction;

        public MissionConditionPartySize(Mission mission, IMissionConditionOwner owner, MissionConditionPrototype prototype) 
            : base(mission, owner, prototype)
        {
            // AchievementSoloTerminalBossesGreen
            _proto = prototype as MissionConditionPartySizePrototype;
            _partySizeChangedAction = OnPartySizeChanged;
        }

        public override bool OnReset()
        {
            foreach (var player in Mission.GetParticipants())
            {
                int partySize = 1;
                var party = player.Party;
                if (party != null) partySize = party.NumMembers;
                if (partySize >= _proto.MinSize && partySize <= _proto.MaxSize)
                {
                    SetCompleted();
                    return true;
                }
            }

            ResetCompleted();
            return true;
        }

        private void OnPartySizeChanged(PartySizeChangedGameEvent evt)
        {
            var player = evt.Player;
            int partySize = evt.PartySize;
            if (player == null || IsMissionPlayer(player) == false) return;
            if (partySize < _proto.MinSize || partySize > _proto.MaxSize) return;

            UpdatePlayerContribution(player);
            SetCompleted();
        }

        public override void RegisterEvents(Region region)
        {
            EventsRegistered = true;
            region.PartySizeChangedEvent.AddActionBack(_partySizeChangedAction);            
        }

        public override void UnRegisterEvents(Region region)
        {
            EventsRegistered = false;
            region.PartySizeChangedEvent.RemoveAction(_partySizeChangedAction);
        }
    }
}