﻿using System.Text;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Entities
{
    public enum WaypointPrototypeId : ulong
    {
        NPEAvengersTowerHub = 10137590415717831231,
        AvengersTowerHub = 15322252936284737788,
    }

    public class Transition : WorldEntity
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private string _transitionName = string.Empty;          // Seemingly unused
        private List<Destination> _destinationList = new();

        public List<Destination> DestinationList { get => _destinationList; }

        public TransitionPrototype TransitionPrototype { get => Prototype as TransitionPrototype; }

        public Transition(Game game) : base(game) { }

        public override bool Initialize(EntitySettings settings)
        {
            base.Initialize(settings);

            // old
            Destination destination = Destination.FindDestination(settings.Cell, TransitionPrototype);

            if (destination != null)
                _destinationList.Add(destination);

            return true;
        }

        public override bool Serialize(Archive archive)
        {
            bool success = base.Serialize(archive);

            //if (archive.IsTransient)
            success &= Serializer.Transfer(archive, ref _transitionName);
            success &= Serializer.Transfer(archive, ref _destinationList);

            return success;
        }

        protected override void BuildString(StringBuilder sb)
        {
            base.BuildString(sb);

            sb.AppendLine($"{nameof(_transitionName)}: {_transitionName}");
            for (int i = 0; i < _destinationList.Count; i++)
                sb.AppendLine($"{nameof(_destinationList)}[{i}]: {_destinationList[i]}");
        }

        public void ConfigureTowerGen(Transition transition)
        {
            Destination destination;

            if (_destinationList.Count == 0)
            {
                destination = new();
                _destinationList.Add(destination);
            }
            else
            {
                destination = _destinationList[0];
            }

            destination.EntityId = transition.Id;
            destination.EntityRef = transition.PrototypeDataRef;
            destination.Type = TransitionPrototype.Type;
        }

        public bool UseTransition(Player player)
        {
            Logger.Debug($"UseTransition(): transitionType={TransitionPrototype.Type}");

            switch (TransitionPrototype.Type)
            {
                case RegionTransitionType.Transition:
                case RegionTransitionType.TowerUp:      // TODO: Separate TowerUp/TowerDown?
                case RegionTransitionType.TowerDown:
                    if (_destinationList.Count == 0)
                        return Logger.WarnReturn(false, "UseTransition(): No available destinations");

                    Destination destination = _destinationList[0];

                    Logger.Trace($"Destination entity {destination.EntityRef.GetName()}");

                    PrototypeId targetRegionProtoRef = destination.RegionRef;

                    if (targetRegionProtoRef != PrototypeId.Invalid && player.PlayerConnection.TransferParams.DestRegionProtoRef != targetRegionProtoRef)
                    {
                        TeleportToTarget(player, destination.RegionRef, destination.TargetRef);
                        return true;
                    }

                    Transition targetTransition;

                    if (destination.EntityId != InvalidId)
                        targetTransition = player.Game.EntityManager.GetEntity<Transition>(destination.EntityId);
                    else
                        targetTransition = player.GetRegion()?.FindTransition(destination.AreaRef, destination.CellRef, destination.EntityRef);

                    TeleportToTransition(player, targetTransition);
                    return true;

                case RegionTransitionType.Waypoint:
                    // TODO: Unlock waypoint
                    return true;

                case RegionTransitionType.ReturnToLastTown:
                    TeleportToLastTown(player);
                    return true;

                default:
                    return Logger.WarnReturn(false, $"UseTransition(): Unimplemented region transition type {TransitionPrototype.Type}");
            }
        }

        private static void TeleportToTarget(Player player, PrototypeId regionProtoRef, PrototypeId targetProtoRef)
        {
            Logger.Trace($"TeleportToRegionConnectionTarget(): Destination region {regionProtoRef.GetNameFormatted()} [{targetProtoRef.GetNameFormatted()}]");
            player.Game.MovePlayerToRegion(player.PlayerConnection, regionProtoRef, targetProtoRef);
        }

        private static bool TeleportToTransition(Player player, Transition transition)
        {
            if (transition == null)
                return Logger.WarnReturn(false, "TeleportToTransition(): target == null");

            Logger.Trace($"TeleportToTransition(): transition=[{transition}]");

            TransitionPrototype targetTransitionProto = transition.TransitionPrototype;
            if (targetTransitionProto == null) return true;

            Vector3 targetPos = transition.RegionLocation.Position;
            Orientation targetRot = transition.RegionLocation.Orientation;
            targetTransitionProto.CalcSpawnOffset(ref targetRot, ref targetPos);
            Logger.Trace($"Transitioning to {targetPos}");

            uint cellId = transition.Properties[PropertyEnum.MapCellId];
            uint areaId = transition.Properties[PropertyEnum.MapAreaId];
            Logger.Trace($"Transitioning to areaId={areaId} cellId={cellId}");

            player.CurrentAvatar.ChangeRegionPosition(targetPos, targetRot, ChangePositionFlags.Teleport);
            return true;
        }

        private static void TeleportToLastTown(Player player)
        {
            // TODO: Teleport to the last saved hub
            Logger.Trace($"TeleportToLastTown(): Destination LastTown");
            player.Game.MovePlayerToRegion(player.PlayerConnection,
                (PrototypeId)RegionPrototypeId.AvengersTowerHUBRegion, (PrototypeId)WaypointPrototypeId.NPEAvengersTowerHub);
        }
    }
}
