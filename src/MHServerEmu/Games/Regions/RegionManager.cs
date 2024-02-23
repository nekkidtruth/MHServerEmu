﻿using MHServerEmu.Common;
using MHServerEmu.Common.Logging;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities;
using MHServerEmu.Games.GameData;

namespace MHServerEmu.Games.Regions
{
    public class RegionManager
    {
        public static bool GenerationAsked;

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly EntityManager _entityManager;
        private static readonly Dictionary<RegionPrototypeId, Region> _regionDict = new();

        public static void ClearRegionDict() => _regionDict?.Clear();

        //----------
        private uint _cellId;
        private uint _areaId;
        private readonly Dictionary<uint, Cell> _allCells = new();
        private readonly Dictionary<ulong, Region> _allRegions = new();
        private readonly Dictionary<ulong, Region> _matches = new();
        public Game Game { get; private set; }
        private readonly object _managerLock = new();
        public RegionManager(EntityManager entityManager)
        {
            _entityManager = entityManager;
            _areaId = 1;
            _cellId = 1;
        }

        public bool Initialize(Game game)
        {
            Game = game;
            return true;
        }

        public uint AllocateCellId() => _cellId++;
        public uint AllocateAreaId() => _areaId++;

        public bool AddCell(Cell cell)
        {
            if (cell != null && _allCells.ContainsKey(cell.Id) == false)
            {
                _allCells[cell.Id] = cell;
                if (cell.Area.Log) Logger.Trace($"Adding cell {cell} in region {cell.GetRegion()} area id={cell.Area.Id}");
                return true;
            }
            return false;
        }

        public Cell GetCell(uint cellId)
        {
            if (_allCells.TryGetValue(cellId, out var cell)) return cell;
            return null;
        }

        public bool RemoveCell(Cell cell)
        {
            if (cell == null) return false;
            if (cell.Area.Log) Logger.Trace($"Removing cell {cell} from region {cell.GetRegion()}");

            if (_allCells.ContainsKey(cell.Id))
            {
                _allCells.Remove(cell.Id);
                return true;
            }
            return false;
        }

        public Region CreateRegion(RegionSettings settings)
        {
            if (settings.RegionDataRef == 0) return null;

            ulong instanceAddress = settings.InstanceAddress;
            if (instanceAddress == 0 || GetRegion(instanceAddress) != null) return null;

            Region region = new(Game);
            if (region == null) return null;

            _allRegions[instanceAddress] = region;

            RegionSettings initSettings = settings; // clone?
            initSettings.InstanceAddress = instanceAddress;

            if (region.Initialize(initSettings) == false)
            {
                _allRegions.Remove(instanceAddress);
                region.Shutdown();
                return null;
            }

            if (region.GetMatchNumber() != 0)
                _matches[region.GetMatchNumber()] = region;

            return region;
        }

        public Region EmptyRegion(RegionPrototypeId prototype)
        {
            Region region = new(prototype, 1038711701,
             Array.Empty<byte>(),
             new(10, DifficultyTier.Normal));
            return region;
        }

        public Region GenerateRegion(RegionPrototypeId prototype) 
        {
            RegionSettings settings = new()
            {
                Seed = Game.Random.Next(),
                DifficultyTierRef = (PrototypeId)DifficultyTier.Normal,
                InstanceAddress = IdGenerator.Generate(IdType.Region),
                Level = 10,
                Bound = Aabb.Zero,
                GenerateAreas = true,
                GenerateEntities = true,
                GenerateLog = false,
                Affixes = new List<PrototypeId>(),
                RegionDataRef = (PrototypeId)prototype
            };
            // settings.Seed = 1776322703;
            // GRandom random = new(settings.Seed);//Game.Random.Next()
            int tries = 10;
            Region region = null;
            while (region == null && (--tries > 0))
            {
                if (tries < 9) settings.Seed = Game.Random.Next(); // random.Next(); 
                region = CreateRegion(settings);
            }

            if (region == null)
                Logger.Error($"GenerateRegion failed after {10 - tries} attempts | regionId: {prototype} | Last Seed: {settings.Seed}");

            return region;
        }

        // NEW
        public Region GetRegion(ulong id)
        {
            if (id == 0) return null;
            lock (_managerLock)
            {
                if (_allRegions.TryGetValue(id, out Region region))
                    return region;
            }
            return null;
        }

        public static Region GetRegion(Game game, ulong id)
        {
            if (game == null) return null;
            RegionManager regionManager = game.RegionManager;
            if (regionManager == null) return null;
            return regionManager.GetRegion(id);
        }

        // OLD
        public Region GetRegion(RegionPrototypeId prototype)
        {
            //  prototype = (RegionPrototypeId)7735172603194383419;
            lock (_managerLock)
            {
                if (_regionDict.TryGetValue(prototype, out Region region) == false)
                {
                    // Generate the region and create entities for it if needed
                    ulong numEntities = _entityManager.PeekNextEntityId();
                    Logger.Debug($"GenerateRegion {GameDatabase.GetFormattedPrototypeName((PrototypeId)prototype)}");
                    region = GenerateRegion(prototype);
                    // region = EmptyRegion(prototype);
                    region.ArchiveData = GetArchiveData(prototype);
                    _entityManager.HardcodedEntities(region);
                    ulong entities = _entityManager.PeekNextEntityId() - numEntities;
                    Logger.Debug($"Entities generated = {entities}");
                    region.CreatedTime = DateTime.Now;

                    _regionDict.Add(prototype, region);

                }

                return region;
            }
        }

        private const int CleanUpTime = 60 * 1000 * 5; // 5 minutes
        private const int UnVisitedTime = 5; // 5 minutes

        public async Task CleanUpRegionsAsync()
        {            
            while (true)
            {
                CleanUpRegions();
                await Task.Delay(CleanUpTime); 
            }
        }

        private void CleanUpRegions()
        {
            lock (_managerLock)
            {
                if (_allRegions.Count == 0) return;
            }            
            var currentTime = DateTime.Now;
            Logger.Debug($"CleanUp");

            // Get PlayerRegions
            var players = ServerManager.Instance.PlayerManagerService.IteratePlayers();
            HashSet<RegionPrototypeId> playerRegions = new();
            foreach (var player in players)
            {
                var regionRef = player.Session.Account.Player.Region; // TODO use RegionID
                playerRegions.Add(regionRef); 
            }

            // Check all regions 
            List<Region> toShutdown = new();
            lock (_managerLock)
            {
                foreach (Region region in _allRegions.Values)
                {
                    DateTime visitedTime;
                    lock (region.Lock)
                    {
                        visitedTime = region.VisitedTime;
                    }
                    TimeSpan timeDifference = currentTime - visitedTime;

                    if (playerRegions.Contains(region.PrototypeId)) // TODO RegionId
                    {
                        // TODO send force exit from region to Players
                    }
                    else
                    {
                        // TODO check all active local teleport to this Region
                        if (timeDifference.TotalMinutes > UnVisitedTime)
                            toShutdown.Add(region);
                    }
                }
            }

            // ShoutDown all unactived regions
            foreach (Region region in toShutdown)
            {
                lock (_managerLock)
                {
                    _allRegions.Remove(region.Id);
                    _regionDict.Remove(region.PrototypeId);
                }
                TimeSpan lifetime = DateTime.Now - region.CreatedTime;
                string formattedLifetime = string.Format("{0:%m} min {0:%s} sec", lifetime);
                Logger.Warn($"Shutdown region = {region}, Lifetime = {formattedLifetime}");
                region.Shutdown();                
            }

        }

        #region Hardcoded
        private static byte[] GetArchiveData(RegionPrototypeId prototype)
        {
            byte[] archiveData = Array.Empty<byte>();

            switch (prototype)
            {

                case RegionPrototypeId.NPEAvengersTowerHUBRegion:

                    archiveData = new byte[] {
                        0xEF, 0x01, 0xE8, 0xC1, 0x02, 0x02, 0x00, 0x00, 0x00, 0x2C, 0xED, 0xC6,
                        0x05, 0x95, 0x80, 0x02, 0x0C, 0x00, 0x04, 0x9E, 0xCB, 0xD1, 0x93, 0xC7,
                        0xE8, 0xAF, 0xCC, 0xEE, 0x01, 0x06, 0x00, 0x8B, 0xE5, 0x02, 0x9E, 0xE6,
                        0x97, 0xCA, 0x0C, 0x01, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x04, 0x9B, 0xB2, 0x81, 0xF2, 0x83, 0xC6, 0xCD, 0x92, 0x10,
                        0x06, 0x00, 0xA2, 0xE0, 0x03, 0xBC, 0x88, 0xA0, 0x89, 0x0E, 0x01, 0x00,
                        0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xCC, 0xD7, 0xD1,
                        0xBE, 0xA9, 0xB0, 0xBB, 0xFE, 0x44, 0x06, 0x00, 0xCF, 0xF3, 0x04, 0xBC,
                        0xA4, 0xAD, 0xD3, 0x0A, 0x01, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0xC3, 0xBE, 0xB9, 0xC8, 0xD6, 0x8F, 0xAF, 0x8C, 0xE7,
                        0x01, 0x06, 0x00, 0xC7, 0x98, 0x05, 0xD6, 0x91, 0xB8, 0xA9, 0x0E, 0x01,
                        0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00
                    };

                    break;

                case RegionPrototypeId.XaviersMansionRegion:

                    archiveData = new byte[] {
                    };

                    /*
                    area.CellList[17].AddEncounter(15374827165380448803, 4, true);
                    area.CellList[15].AddEncounter(8642336607468261979, 7, true);
                    area.CellList[23].AddEncounter(4065272706848002543, 3, true);
                    area.CellList[10].AddEncounter(12198525011368022752, 1, true);
                    */

                    break;

                case RegionPrototypeId.DangerRoomHubRegion:

                    archiveData = new byte[] {
                        0xEF, 0x01, 0xA8, 0x9B, 0x02, 0x07, 0x00, 0x00, 0x00, 0xB6, 0x80, 0x01,
                        0xE6, 0xCC, 0x99, 0xFB, 0x03, 0x2C, 0xFC, 0xA9, 0x02, 0xCA, 0x80, 0x03,
                        0xE6, 0xCC, 0x99, 0xFB, 0x03, 0x95, 0x80, 0x02, 0x12, 0xCA, 0x40, 0xE6,
                        0xCC, 0x99, 0xFB, 0x03, 0xA8, 0x80, 0x02, 0x80, 0x80, 0x80, 0x84, 0x04,
                        0xA8, 0xC0, 0x02, 0x9A, 0xB3, 0xE6, 0xF4, 0x03, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00
                    };

                    break;

                case RegionPrototypeId.XManhattanRegion60Cosmic:

                    archiveData = new byte[] {
                        0xEF, 0x01, 0xCF, 0x8F, 0x01, 0x07, 0x00, 0x00, 0x00, 0xB6, 0x80, 0x01,
                        0x9A, 0xB3, 0xE6, 0x80, 0x04, 0x2C, 0x88, 0x18, 0xCA, 0x80, 0x03, 0x9A,
                        0xB3, 0xE6, 0x80, 0x04, 0x95, 0x80, 0x02, 0x1A, 0xCA, 0x40, 0x9A, 0xB3,
                        0xE6, 0x80, 0x04, 0xA8, 0x80, 0x02, 0x80, 0x80, 0x80, 0x88, 0x04, 0xA8,
                        0xC0, 0x02, 0xB8, 0xBD, 0x94, 0xF0, 0x03, 0x00, 0x16, 0xAE, 0xD6, 0xFD,
                        0xEF, 0xD6, 0x84, 0xE1, 0x9B, 0x83, 0x01, 0x08, 0x00, 0xE5, 0x91, 0x01,
                        0x00, 0x05, 0x00, 0x00, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
                        0x01, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x02, 0x02, 0x06, 0x00,
                        0x00, 0x01, 0x01, 0x00, 0x00, 0x03, 0x03, 0x06, 0x00, 0x00, 0x01, 0x01,
                        0x00, 0x00, 0x04, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E,
                        0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2, 0x21,
                        0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE, 0x22,
                        0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24,
                        0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0x05, 0x8E, 0xDA, 0xC2, 0x88, 0xFD,
                        0xE7, 0x87, 0xD1, 0x2D, 0x0A, 0xA0, 0x9F, 0x93, 0xD5, 0xF4, 0xAF, 0x49,
                        0xFF, 0xE3, 0x01, 0x96, 0xCE, 0x96, 0x91, 0x0F, 0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xCB, 0x98, 0xD1, 0xBC, 0xF7,
                        0xB6, 0xE8, 0x8A, 0x22, 0x0A, 0xE0, 0xB1, 0xE9, 0xD9, 0xF4, 0xAF, 0x49,
                        0xD4, 0xF1, 0x02, 0xB8, 0xC2, 0xD1, 0xA7, 0x0D, 0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xF4, 0xA5, 0x8C, 0x85, 0xC3,
                        0xAA, 0xC9, 0xCE, 0xDF, 0x01, 0x06, 0x00, 0xFE, 0x91, 0x01, 0xEE, 0x95,
                        0x80, 0xA3, 0x0C, 0x02, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x01, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0xE4,
                        0x82, 0x1E, 0xE1, 0xD9, 0x20, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0x99,
                        0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24, 0xD9, 0xDA, 0x24, 0x8F,
                        0x99, 0x25, 0xC1, 0xFD, 0xA4, 0xD3, 0x8E, 0x9C, 0xE1, 0xCA, 0x55, 0x08,
                        0x00, 0xFA, 0xE6, 0x01, 0x00, 0x02, 0x00, 0x00, 0x06, 0x00, 0x00, 0x05,
                        0x05, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x0E, 0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2,
                        0x21, 0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE,
                        0x22, 0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF,
                        0x24, 0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0x98, 0x8A, 0xBD, 0xC5, 0xE4,
                        0xE9, 0xB8, 0xA0, 0xEF, 0x01, 0x08, 0x00, 0xB0, 0x87, 0x03, 0x00, 0x05,
                        0x00, 0x00, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x06,
                        0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x02, 0x02, 0x06, 0x00, 0x00, 0x01,
                        0x01, 0x00, 0x00, 0x03, 0x03, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
                        0x04, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0xE4, 0x82,
                        0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2, 0x21, 0xE1, 0xB2,
                        0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE, 0x22, 0x9D, 0xB3,
                        0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24, 0xD9, 0xDA,
                        0x24, 0x8F, 0x99, 0x25, 0x05, 0xFB, 0x8B, 0x9C, 0x86, 0xB3, 0x90, 0xFD,
                        0xB7, 0x26, 0x06, 0x00, 0x8F, 0xCF, 0x04, 0xCE, 0x9F, 0xBD, 0x95, 0x0B,
                        0x03, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01,
                        0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02, 0x04, 0x00, 0x00,
                        0x00, 0x14, 0x00, 0x00, 0x0E, 0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1,
                        0xD9, 0x20, 0x87, 0xA2, 0x21, 0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E,
                        0xF7, 0x22, 0xCC, 0xFE, 0x22, 0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1,
                        0xB4, 0x24, 0xDF, 0xCF, 0x24, 0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0xEB,
                        0xF0, 0xE8, 0x8A, 0xA1, 0x82, 0xBB, 0xFB, 0x5D, 0x0A, 0x00, 0x80, 0xB0,
                        0x01, 0xD6, 0xFA, 0xC8, 0x8F, 0x09, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x01, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
                        0x00, 0x02, 0x02, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x03, 0x03,
                        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x04, 0x04, 0x00, 0x00, 0x00,
                        0x00, 0x01, 0x00, 0x00, 0x05, 0x05, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x0E, 0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87,
                        0xA2, 0x21, 0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC,
                        0xFE, 0x22, 0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF,
                        0xCF, 0x24, 0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0x96, 0x89, 0xD9, 0xD8,
                        0xFA, 0xFC, 0xB7, 0xE9, 0x43, 0x0A, 0xA0, 0x86, 0xAD, 0xDD, 0xF4, 0xAF,
                        0x49, 0xA1, 0xB2, 0x02, 0xFA, 0xEF, 0xE1, 0xE0, 0x08, 0x01, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE9, 0x89, 0xC5, 0xA0,
                        0xF2, 0xC5, 0xE2, 0xFD, 0x4B, 0x0A, 0xA0, 0xD3, 0xD1, 0x80, 0xF5, 0xAF,
                        0x49, 0x9F, 0xC2, 0x02, 0xC4, 0xAE, 0xBE, 0xDA, 0x0E, 0x02, 0x00, 0x00,
                        0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x01, 0x99, 0xA1, 0x24, 0xC9, 0xA3, 0xE9, 0xA6,
                        0xC9, 0x87, 0xCD, 0x9F, 0x4F, 0x08, 0x00, 0xAA, 0xD9, 0x02, 0x00, 0x04,
                        0x00, 0x00, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x06,
                        0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x02, 0x02, 0x06, 0x00, 0x00, 0x01,
                        0x01, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x0E, 0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2,
                        0x21, 0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE,
                        0x22, 0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF,
                        0x24, 0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0x05, 0x9A, 0xBB, 0xAF, 0xC8,
                        0x86, 0xEF, 0xD4, 0x92, 0x09, 0x06, 0x00, 0xD5, 0xF3, 0x04, 0xB6, 0x9B,
                        0xA3, 0xC3, 0x0E, 0x03, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x01, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02,
                        0x02, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x00, 0x9D, 0xF0, 0xE0, 0xE4,
                        0xEE, 0xD6, 0x87, 0xF4, 0x11, 0x06, 0x00, 0x81, 0xCF, 0x02, 0xB2, 0xBC,
                        0xF7, 0x87, 0x03, 0x03, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x01, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02,
                        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA3, 0xB0, 0xE1, 0x97,
                        0x98, 0xB3, 0xB7, 0xB9, 0xA0, 0x01, 0x08, 0x00, 0xF4, 0x90, 0x03, 0x00,
                        0x05, 0x00, 0x00, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01,
                        0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x02, 0x02, 0x06, 0x00, 0x00,
                        0x01, 0x01, 0x00, 0x00, 0x03, 0x03, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00,
                        0x00, 0x04, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0xE4,
                        0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2, 0x21, 0xE1,
                        0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE, 0x22, 0x9D,
                        0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24, 0xD9,
                        0xDA, 0x24, 0x8F, 0x99, 0x25, 0xBA, 0xAE, 0xA3, 0xC0, 0xB8, 0xF7, 0x95,
                        0x80, 0xA6, 0x01, 0x06, 0x00, 0xF3, 0x97, 0x03, 0x8A, 0xAD, 0xC7, 0xF4,
                        0x01, 0x03, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
                        0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02, 0x02, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFE, 0xBC, 0x8D, 0xB4, 0xAF, 0xAD,
                        0xEF, 0xB3, 0x62, 0x06, 0x00, 0xCF, 0x9E, 0x03, 0xC0, 0xFD, 0xBE, 0x9D,
                        0x07, 0x03, 0x00, 0x00, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01,
                        0x01, 0x04, 0x00, 0x00, 0x02, 0x04, 0x00, 0x00, 0x02, 0x02, 0x04, 0xC0,
                        0xF2, 0xEA, 0xCB, 0xF5, 0xAF, 0x49, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E,
                        0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2, 0x21,
                        0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE, 0x22,
                        0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24,
                        0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0x05, 0xDA, 0xC9, 0xFD, 0xFC, 0x8E,
                        0x9C, 0xEF, 0xBB, 0xD1, 0x01, 0x08, 0x00, 0xD0, 0xA6, 0x03, 0x00, 0x05,
                        0x00, 0x00, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x06,
                        0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x02, 0x02, 0x06, 0x00, 0x00, 0x01,
                        0x01, 0x00, 0x00, 0x03, 0x03, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
                        0x04, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0xE4, 0x82,
                        0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2, 0x21, 0xE1, 0xB2,
                        0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE, 0x22, 0x9D, 0xB3,
                        0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24, 0xD9, 0xDA,
                        0x24, 0x8F, 0x99, 0x25, 0x83, 0xE3, 0xB9, 0x8A, 0xD7, 0xDB, 0xE2, 0xED,
                        0xB2, 0x01, 0x08, 0x00, 0x86, 0xDD, 0x04, 0x00, 0x07, 0x00, 0x00, 0x06,
                        0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x06, 0x00, 0x00, 0x01,
                        0x01, 0x00, 0x00, 0x02, 0x02, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00,
                        0x03, 0x03, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x04, 0x04, 0x06,
                        0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x05, 0x05, 0x06, 0x00, 0x00, 0x01,
                        0x01, 0x00, 0x00, 0x06, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x0E, 0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2,
                        0x21, 0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE,
                        0x22, 0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF,
                        0x24, 0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0xF8, 0xFD, 0xA4, 0xBD, 0xD2,
                        0xCA, 0xC2, 0xA2, 0x0D, 0x02, 0x00, 0x8B, 0xB5, 0x03, 0x00, 0x00, 0x0E,
                        0xE4, 0x82, 0x1E, 0xAC, 0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2, 0x21,
                        0xE1, 0xB2, 0x21, 0x9A, 0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE, 0x22,
                        0x9D, 0xB3, 0x23, 0x99, 0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24,
                        0xD9, 0xDA, 0x24, 0x8F, 0x99, 0x25, 0xA0, 0xD9, 0xEC, 0x92, 0xDA, 0x8C,
                        0xED, 0xE9, 0xC8, 0x01, 0x06, 0x00, 0xB3, 0xE0, 0x03, 0xEA, 0x8F, 0xB2,
                        0xC0, 0x02, 0x03, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x01, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02, 0x02,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xAD, 0xE6, 0x85, 0xFD, 0xEB,
                        0xC3, 0xBC, 0x9A, 0x26, 0x06, 0x00, 0xBA, 0xE9, 0x04, 0xF6, 0x98, 0x99,
                        0xBD, 0x06, 0x03, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x01, 0x01, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x02, 0x04,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x9E, 0xF7, 0x22, 0xD9, 0xDA,
                        0x24, 0x03, 0xF0, 0x96, 0x8D, 0xE1, 0xD2, 0xE4, 0xA6, 0xF0, 0xA3, 0x01,
                        0x08, 0x00, 0xD2, 0xBB, 0x05, 0x00, 0x04, 0x00, 0x00, 0x06, 0x00, 0x00,
                        0x01, 0x01, 0x00, 0x00, 0x01, 0x01, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00,
                        0x00, 0x02, 0x02, 0x06, 0x00, 0x00, 0x01, 0x01, 0x00, 0x00, 0x03, 0x03,
                        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0xE4, 0x82, 0x1E, 0xAC,
                        0xC0, 0x1F, 0xE1, 0xD9, 0x20, 0x87, 0xA2, 0x21, 0xE1, 0xB2, 0x21, 0x9A,
                        0xA0, 0x22, 0x9E, 0xF7, 0x22, 0xCC, 0xFE, 0x22, 0x9D, 0xB3, 0x23, 0x99,
                        0xA1, 0x24, 0xF1, 0xB4, 0x24, 0xDF, 0xCF, 0x24, 0xD9, 0xDA, 0x24, 0x8F,
                        0x99, 0x25, 0x00, 0x02, 0xBF, 0x9B, 0x02, 0xCF, 0x9E, 0x03, 0x00, 0xBB,
                        0x8A, 0x8C, 0xC2, 0x92, 0xC7, 0xA2, 0xD2, 0x71, 0x00, 0xBD, 0xD7, 0x03,
                        0xCF, 0x9E, 0x03, 0x00, 0x02, 0x02, 0x00, 0xC8, 0x88, 0x99, 0x95, 0xB2,
                        0x09, 0x00, 0x00
                    };

                    break;
            }

            return archiveData;
        }
        #endregion
    }
}

