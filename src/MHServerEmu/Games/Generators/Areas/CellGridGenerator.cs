﻿using MHServerEmu.Common;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.Generators.Prototypes;
using MHServerEmu.Games.Generators.Regions;
using MHServerEmu.Games.Regions;

namespace MHServerEmu.Games.Generators.Areas
{
    public class CellGridGenerator : BaseGridAreaGenerator
    {
        private enum ProcessEnum
        {
            Initialize,
            Generate
        }

        public class CellDeterminationMap : Dictionary<Cell.Type, List<Point2>> {}

        public override bool InitializeContainer()
        {
            if (!base.InitializeContainer()) return false;

            if (Area.AreaPrototype.Generator is not GridAreaGeneratorPrototype proto) return false;

            if (proto.Behaviors != null)
            {
                RunBehaviors(null, proto.Behaviors, ProcessEnum.Initialize);
            }

            return true;
        }

        private void RunBehaviors(GRandom random, CellGridBehaviorPrototype[] behaviors, ProcessEnum process)
        {
            if (behaviors == null)  return;

            foreach (var behaviorProto in behaviors)
            {
                if (behaviorProto == null) continue;
                DetermineAndRunBehavior(random, behaviorProto, process);
            }
        }

        private void DetermineAndRunBehavior(GRandom random, CellGridBehaviorPrototype behaviorProto, ProcessEnum process)
        {
            if (behaviorProto == null) return;

            string behaviorIdString = GameDatabase.GetAssetName(behaviorProto.BehaviorId);

            if (behaviorIdString.Equals("CellGridBlacklistBehavior"))
            {
                if (process == ProcessEnum.Initialize)
                {
                    if (behaviorProto is CellGridBlacklistBehaviorPrototype behavior)
                    {
                        if (!CellGridBlacklistBehavior(behavior))
                            Logger.Error("DetermineAndRunBehavior CellGridBlacklistBehavior");
                    }
                }
            }
            else if (behaviorIdString.Equals("CellGridRampBehavior"))
            {
                if (process == ProcessEnum.Initialize)
                {
                    if (behaviorProto is CellGridRampBehaviorPrototype behavior)
                    {
                        if (!CellGridRampBehavior(behavior))
                            Logger.Error("DetermineAndRunBehavior CellGridRampBehavior");
                    }
                }
            }
        }

        private bool CellGridRampBehavior(CellGridRampBehaviorPrototype behavior)
        {
            if (CellContainer == null) return false;

            Cell.Type startEdge = GetEdgeFromAssetName(behavior.EdgeStart);
            Cell.Type endEdge = GetEdgeFromAssetName(behavior.EdgeEnd);

            if (startEdge == Cell.Type.W && endEdge == Cell.Type.E)
            {
                IncrementZ = 0.0f;
                IncrementX = 0.0f;
                IncrementY = behavior.Increment;
                return true;
            }
            if (startEdge == Cell.Type.S && endEdge == Cell.Type.N)
            {
                IncrementZ = 0.0f;
                IncrementX = behavior.Increment;
                IncrementY = 0.0f;
                return true;
            }
            if (startEdge == Cell.Type.E && endEdge == Cell.Type.W)
            {
                IncrementZ = behavior.Increment * (CellContainer.Height - 1);
                IncrementX = 0.0f;
                IncrementY = -behavior.Increment;
                return true;
            }
            if (startEdge == Cell.Type.N && endEdge == Cell.Type.S)
            {
                IncrementZ = behavior.Increment * (CellContainer.Width - 1);
                IncrementX = -behavior.Increment;
                IncrementY = 0.0f;
                return true;
            }

            return false;
        }

        private static Cell.Type GetEdgeFromAssetName(ulong edge)
        {
            if (edge != 0 && Enum.TryParse(GameDatabase.GetAssetName(edge), out Cell.Type edgeType))
                    return edgeType;            

            return Cell.Type.None;
        }

        private bool CellGridBlacklistBehavior(CellGridBlacklistBehaviorPrototype behavior)
        {
            if (CellContainer == null || behavior.Blacklist == null) return false;

            bool success = true;
            foreach (var cell in behavior.Blacklist)
            {
                if (cell != null 
                    && CellContainer.DestroyableCell(cell.X, cell.Y) 
                    && CellContainer.DestroyCell(cell.X, cell.Y)) 
                    success = false;
            }

            return success;
        }

        public override bool Initialize(Area area)
        {
            CellContainer = new();

            return base.Initialize(area);
        }

        public override bool Generate(GRandom random, RegionGenerator regionGenerator, List<ulong> areas)
        {
            if (CellContainer == null) return false;

            if (Area.AreaPrototype.Generator is not GridAreaGeneratorPrototype proto) return false;

            bool success = false;
            int tries = 10;

            while (!success && (--tries > 0))
            {
                success = InitializeContainer()
                    && EstablishExternalConnections()
                    && GenerateRandomInstanceLinks(random)
                    && CreateRequiredCells(random, regionGenerator, areas);
            }

            if (!success)
            {
                Logger.Trace($"GridAreaGenerator failed after {10 - tries} attempts\nregion: {Region}\narea: {Area}");
                return false;
            }

            RunBehaviors(random, proto.Behaviors, ProcessEnum.Generate);

            ProcessDeleteExtraneousCells(random, (int)proto.RoomKillChancePct);
            ProcessDeleteExtraneousConnections(random, (int)proto.ConnectionKillChancePct);
            ProcessRegionConnectionsAndDepth();
            ProcessAssignUniqueCellIds();
            ProcessCellPositions(proto.CellSize);

            return ProcessCellTypes(random);
        }

        private void ProcessDeleteExtraneousConnections(GRandom random, int chance)
        {
            if (CellContainer == null) return;

            foreach (GenCell cell in CellContainer)
                if (cell != null) CellContainer.DestroyUnrequiredConnections(cell, random, chance);
        }


        private bool ProcessCellTypes(GRandom random)
        {
            if (CellContainer == null) return false;

            if (Area.AreaPrototype.Generator is not GridAreaGeneratorPrototype proto) return false;

            int randomSeed = Area.RandomSeed;

            if (!BuildCellDeterminationMap(out CellDeterminationMap cellMap)) return false;

            foreach (var item in cellMap)
            {
                List<Point2> coordsOfType = item.Value;
                if (coordsOfType == null) continue;

                Picker<Point2> picker = new (random);
                foreach (Point2 coords in coordsOfType)
                    picker.Add(coords);

                while (!picker.Empty() && picker.PickRemove(out Point2 point))
                {
                    GenCell genCell = CellContainer.GetCell(point.X, point.Y);
                    if (genCell != null)
                    {
                        List<uint> connectedCells = new ();
                        CreateConnectedCellList(genCell, connectedCells);

                        if (genCell.CellRef != 0)
                        {
                            CellSettings cellSettings = new() 
                            {
                                CellRef = genCell.CellRef,
                                PositionInArea = new(genCell.Position),
                                Seed = ++randomSeed,
                                ConnectedCells = connectedCells,
                                PopulationThemeOverrideRef = genCell.PopulationThemeOverrideRef
                            };

                            Area.AddCell(genCell.Id, cellSettings); // Verify
                            continue;
                        }
                        else
                        {
                            Cell.Type cellType = CellContainer.DetermineType(point.X, point.Y);
                            ulong cellRef = 0;

                            List<ulong> excludedList = new ();
                            BuildExcludedListLikeCells(point.X, point.Y, cellType, excludedList);

                            if (CellSetRegistry.HasCellOfType(cellType))
                            {
                                cellRef = CellSetRegistry.GetCellSetAssetPicked(random, cellType, excludedList);
                            }

                            if (cellRef == 0)
                            {
                                Logger.Trace($"Generator for Area {Area} tried to pick cell of type {cellType}, none were available. Region: {Region}");
                                return false;
                            }

                            CellSettings cellSettings = new()
                            {
                                CellRef = cellRef,
                                PositionInArea = new(genCell.Position),
                                Seed = ++randomSeed,
                                ConnectedCells = connectedCells,
                                PopulationThemeOverrideRef = genCell.PopulationThemeOverrideRef
                            };

                            Area.AddCell(genCell.Id, cellSettings); // Verify
                            genCell.SetCellRef(cellRef);
                        }
                    }
                    else
                    {
                        PlaceFillerRoom(random, point.X, point.Y, new (point.X * proto.CellSize, point.Y * proto.CellSize, 0.0f));
                    }
                }
            }

            CleanCellDeterminationMap(ref cellMap);

            return true;
        }

        private bool BuildCellDeterminationMap(out CellDeterminationMap cellMap)
        {
            cellMap = new();
            if (cellMap == null) return false;

            for (int y = 0; y < CellContainer.Height; y++)
            {
                for (int x = 0; x < CellContainer.Width; x++)
                {
                    Cell.Type type = CellContainer.DetermineType(x, y);

                    if (cellMap.TryGetValue(type, out List<Point2> pointList))
                    {
                        pointList.Add(new(x, y));
                    }
                    else
                    {
                        pointList = new() { new(x, y) };
                        cellMap[type] = pointList;
                    }
                }
            }

            return true;
        }

        public static void CleanCellDeterminationMap(ref CellDeterminationMap cellMap)
        {
            if (cellMap != null)
            {
                foreach (var entry in cellMap)
                    entry.Value.Clear();

                cellMap.Clear();
                cellMap = null;
            }
        }

        private bool PlaceFillerRoom(GRandom random, int x, int y, Vector3 position)
        {
            Cell.Filler filler = Cell.Filler.None;

            if (CellContainer.GetCell(x + 1, y, false) != null)     filler |= Cell.Filler.N;
            if (CellContainer.GetCell(x + 1, y + 1, false) != null) filler |= Cell.Filler.NE;
            if (CellContainer.GetCell(x, y + 1, false) != null)     filler |= Cell.Filler.E;
            if (CellContainer.GetCell(x - 1, y + 1, false) != null) filler |= Cell.Filler.SE;
            if (CellContainer.GetCell(x - 1, y, false) != null)     filler |= Cell.Filler.S;
            if (CellContainer.GetCell(x - 1, y - 1, false) != null) filler |= Cell.Filler.SW;
            if (CellContainer.GetCell(x, y - 1, false) != null)     filler |= Cell.Filler.W;
            if (CellContainer.GetCell(x + 1, y - 1, false) != null) filler |= Cell.Filler.NW;

            ulong cellRef = CellSetRegistry.GetCellSetAssetPickedByFiller(random, filler);
            if (cellRef == 0)
                cellRef = CellSetRegistry.GetCellSetAssetPickedByFiller(random, Cell.Filler.None);

            if (cellRef != 0)
            {
                CellSettings cellSettings = new ()
                {
                    PositionInArea = position,
                    CellRef = cellRef
                };

                return Area.AddCell(AllocateCellId(), cellSettings) != null;
            }

            return false;
        }

        private void BuildExcludedListLikeCells(int x, int y, Cell.Type cellType, List<ulong> excludedList)
        {
            if (CellContainer == null) return;

            GenCell testCell;

            if (CellContainer.TestCoord(x + 1, y))
            {
                testCell = CellContainer.GetCell(x + 1, y);
                if (testCell != null && testCell.CellRef != 0 && CellContainer.DetermineType(x + 1, y) == cellType)
                    excludedList.Add(testCell.CellRef);
            }

            if (CellContainer.TestCoord(x, y + 1))
            {
                testCell = CellContainer.GetCell(x, y + 1);
                if (testCell != null && testCell.CellRef != 0 && CellContainer.DetermineType(x, y + 1) == cellType)
                    excludedList.Add(testCell.CellRef);
            }

            if (CellContainer.TestCoord(x - 1, y))
            {
                testCell = CellContainer.GetCell(x - 1, y);
                if (testCell != null && testCell.CellRef != 0 && CellContainer.DetermineType(x - 1, y) == cellType)
                    excludedList.Add(testCell.CellRef);
            }

            if (CellContainer.TestCoord(x, y - 1))
            {
                testCell = CellContainer.GetCell(x, y - 1);
                if (testCell != null && testCell.CellRef != 0 && CellContainer.DetermineType(x, y - 1) == cellType)
                    excludedList.Add(testCell.CellRef);
            }
        }

        public static bool CellGridBorderBehavior(Area area)
        {
            if (area == null) return false;

            CellGridBorderBehaviorPrototype borderBehaviorProto = null;
            GeneratorPrototype generatorProto = area.AreaPrototype.Generator;
            GridAreaGeneratorPrototype gridAreaGeneratorProto = generatorProto as GridAreaGeneratorPrototype;

            if (gridAreaGeneratorProto != null && gridAreaGeneratorProto.Behaviors != null)
                borderBehaviorProto = gridAreaGeneratorProto.Behaviors.Last() as CellGridBorderBehaviorPrototype;

            if (borderBehaviorProto == null || gridAreaGeneratorProto.CellSets == null) return true;

            CellSetRegistry registry = new ();
            registry.Initialize(true);
            foreach (var cellSetEntry in gridAreaGeneratorProto.CellSets)
            {
                if (cellSetEntry == null) continue;
                registry.LoadDirectory(cellSetEntry.CellSet, cellSetEntry, cellSetEntry.Weight, cellSetEntry.Unique);
            }

            return DoBorderBehavior(area, borderBehaviorProto.BorderWidth, registry, gridAreaGeneratorProto.CellSize, gridAreaGeneratorProto.CellsX, gridAreaGeneratorProto.CellsY);
        }

        private static bool DoBorderBehavior(Area area, int borderWidth, CellSetRegistry registry, float cellSize, int cellsX, int cellsY)
        {
            GRandom random = area.Game.GetRandom();     
            Vector3 origin = area.Origin;
            Vector3 offset = new();
            Cell.Filler filler;

            for (int w = 1; w <= borderWidth; w++)
            {
                for (int x = 0; x < cellsX + borderWidth; x++)
                {
                    filler = Cell.Filler.E;
                    if (x == (cellsX + borderWidth - 1)) filler = Cell.Filler.SE;

                    offset.X = x * cellSize;
                    offset.Y = -w * cellSize;
                    TryPlaceBorderBehaviorCell(area, registry, random, origin, offset, filler);

                    filler = Cell.Filler.W;
                    if (x == (cellsX + borderWidth - 1)) filler = Cell.Filler.NW;

                    offset.X = (cellsX - x - 1) * cellSize;
                    offset.Y = (cellsY + w - 1) * cellSize;
                    TryPlaceBorderBehaviorCell(area, registry, random, origin, offset, filler);
                }

                for (int y = 0; y < cellsY + borderWidth; y++)
                {
                    filler = Cell.Filler.S;
                    if (y == (cellsY + borderWidth - 1)) filler = Cell.Filler.SW;

                    offset.X = (cellsX + w - 1) * cellSize;
                    offset.Y = y * cellSize;
                    TryPlaceBorderBehaviorCell(area, registry, random, origin, offset, filler);

                    filler = Cell.Filler.N;
                    if (y == (cellsY + borderWidth - 1)) filler = Cell.Filler.NE;

                    offset.X = -w * cellSize;
                    offset.Y = (cellsY - y - 1) * cellSize;
                    TryPlaceBorderBehaviorCell(area, registry, random, origin, offset, filler);
                }
            }

            return true;
        }

        private static bool TryPlaceBorderBehaviorCell(Area area, CellSetRegistry registry, GRandom random, Vector3 origin, Vector3 offset, Cell.Filler fillerType)
        {
            if (area == null) return false;

            uint areaId = area.Id;
            Region region = area.Region;

            Vector3 position = origin + offset;
            bool inAreas = true;
            Aabb cellBounds = registry.CellBounds;

            foreach (Area testArea in region.AreaList) // IterateAreas()
            {
                Aabb regionBounds = testArea.RegionBounds;
                Aabb testBounds = new (position, cellBounds.Width - 128.0f, cellBounds.Length - 128.0f, cellBounds.Height - 128.0f);

                if (regionBounds.Intersects(testBounds) && testArea.Id != areaId)
                {
                    inAreas = false;
                    break;
                }
            }

            if (inAreas)
            {
                ulong dynamicAreaRef = GameDatabase.GetGlobalsPrototype().DynamicArea;
                Area dynamicArea = region.CreateArea(dynamicAreaRef, position);
                AreaGenerationInterface generatorInterface = dynamicArea.GetAreaGenerationInterface();

                if (generatorInterface != null)
                {
                    ulong cellRef = registry.GetCellSetAssetPickedByFiller(random, fillerType);
                    if (cellRef == 0) cellRef = registry.GetCellSetAssetPickedByFiller(random, Cell.Filler.None);

                    if (cellRef != 0)
                    {
                        generatorInterface.PlaceCell(cellRef, new());
                        area.AddSubArea(dynamicArea);

                        List<ulong> areas = new() { area.GetPrototypeDataRef() };
                        dynamicArea.Generate(null, areas, GenerateFlag.Background);
                        return true;
                    }
                    else
                    {
                        region.DestroyArea(dynamicArea.Id);
                    }
                }
            }

            return false;
        }

    }
}
