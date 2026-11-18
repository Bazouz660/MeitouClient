# State inventory of the simulation (master 315d26ecf)

Made with `inventory-tool/` (Roslyn semantic model over `src/Meitou.Simulation` and `src/Meitou.Game`). One row per field or auto-property declared in the simulation: `state-inventory.tsv`.

How the flags are decided (heuristics, read them as leads): **hashed** = read inside a method that takes a `StateHasher` or has "Hash" in its or its type's name; **saved** = read in a file under `Saving/` or named `*Save.cs`; **loaded** = written in such a file; **published** = read in `WorldPublish*.cs` or a method named `Publish*`. A write is an assignment, an increment or a `ref`/`out` pass outside constructors and initialisers; a content write is an indexer assignment or a call to a common mutator (`Add`, `Remove`, `Clear`, ...) on the member. The game compilation had 781 binding errors (generated interop code is missing), so a few game reads may be uncounted.

## Headline

- State members: 5,856; changed after construction: 2,361; static and changed: 32.
- Among the changed ones: hashed 683, saved 307, loaded 280, published 248.
- The game reads simulation state at 5,837 places (1,172 members): 3,167 on snapshot types, 2,670 on live types (731 members). The second group is what a client without the simulation cannot do.
- The game writes simulation state at 651 places (163 members); 327 of them build `SimCommand` objects.

## Largest state types

| Type | Members | Changed after construction |
|---|---|---|
| CharacterCold | 218 | 201 |
| CharacterSnapshot | 103 | 102 |
| World | 99 | 67 |
| PopulationSystem | 82 | 64 |
| AiMind | 71 | 62 |
| ProductionSystem | 69 | 54 |
| CombatSlot | 66 | 64 |
| WorldSnapshot | 59 | 0 |
| Scan | 58 | 12 |
| Blackboard | 57 | 54 |
| FixtureRegistry | 50 | 49 |
| AiSystem | 45 | 31 |
| Squad | 45 | 35 |
| Fixture | 44 | 17 |
| Machine | 44 | 33 |
| Platoon | 43 | 36 |
| ItemInstance | 43 | 26 |
| CombatSystem | 38 | 22 |
| PlayerState | 37 | 30 |
| MedicalState | 35 | 30 |
| ZoneGeometry | 35 | 20 |
| TradeItemView | 35 | 0 |
| DialogueSystem | 33 | 20 |
| AnimationSystem | 30 | 9 |
| PopulationSettings | 30 | 13 |

## Live (non-snapshot) types the game reads

| Type | Members read | Read sites |
|---|---|---|
| World | 29 | 503 |
| CharacterId | 3 | 111 |
| PopulationData | 8 | 88 |
| PlayerState | 16 | 80 |
| TradeItemView | 33 | 74 |
| Fixture | 12 | 62 |
| EfficiencyView | 11 | 56 |
| BloodMark | 8 | 55 |
| ResearchOption | 13 | 46 |
| ContainerSpot | 8 | 45 |
| HeldItemView | 13 | 43 |
| PowerView | 12 | 43 |
| FarmView | 13 | 41 |
| MachineSlotView | 10 | 41 |
| BuildingFootprint | 2 | 40 |
| CageSpot | 10 | 39 |
| DoorPick | 10 | 39 |
| TradeView | 13 | 39 |
| PlacedBuilding | 3 | 35 |
| TownSite | 3 | 34 |
| BedSpot | 6 | 33 |
| TurretPanelView | 15 | 32 |
| CraftOptionView | 7 | 30 |
| CombatSlot | 14 | 26 |
| ProspectReport | 11 | 25 |
| SeatSpot | 7 | 24 |
| Floater | 10 | 23 |
| TickProfile | 5 | 23 |
| Squad | 2 | 23 |
| CharacterCold | 14 | 21 |
| MaterialView | 3 | 21 |
| CharacterHot | 8 | 20 |
| GroundItem | 4 | 20 |
| TrainingSpot | 4 | 20 |
| SoundEvent | 10 | 19 |
| StandardSystemSet | 3 | 19 |
| ModSkill | 6 | 19 |
| RuinedBuilding | 2 | 19 |
| NavSystem | 2 | 18 |
| ZoneNavMesh | 6 | 18 |

## Game files that read live simulation state most

| File | Read sites |
|---|---|
| ProductionWindows.cs | 220 |
| BuildingSelection.cs | 156 |
| AgentWorld.cs | 144 |
| FurnitureUse.cs | 144 |
| AgentOrders.cs | 143 |
| InventoryWindows.cs | 100 |
| ContextMenuWindow.cs | 99 |
| DebugMenu.cs | 92 |
| TradeWindows.cs | 82 |
| GameHost.cs | 81 |
| TooltipRuntime.cs | 77 |
| GameSaves.cs | 71 |
| BuildingSync.cs | 70 |
| ScreenshotScenes.cs | 55 |
| AgentPlay.cs | 54 |
| BattleScenario.cs | 46 |
| BloodDecals.cs | 43 |
| ProspectingPanel.cs | 36 |
| SandboxScenario.cs | 35 |
| GameHost.Characters.cs | 32 |
| FloaterText.cs | 32 |
| HudPanels.cs | 30 |
| FarmPanel.cs | 30 |
| GameHost.FrameShared.cs | 26 |
| WorldMap.cs | 26 |
| Debris.cs | 25 |
| BuildMode.cs | 25 |
| AgentCombatWatch.cs | 24 |
| WorldSession.cs | 24 |
| WorldPicking.cs | 23 |

## Simulation types the game writes

| Type | Members written | Write sites |
|---|---|---|
| SimCommand | 1 | 327 |
| World | 16 | 120 |
| StandardSystemOptions | 16 | 16 |
| OrderCommand | 2 | 16 |
| PopulationSettings | 13 | 14 |
| Fixture | 4 | 12 |
| AttackOrder | 3 | 11 |
| BodyOptions | 8 | 9 |
| CombatOptions | 9 | 9 |
| ItemInstance | 7 | 7 |
| LockState | 3 | 7 |
| SimulationExtensions | 6 | 6 |
| WorldSettings | 6 | 6 |
| PopulationSystem | 2 | 6 |
| SaveCaptureOptions | 6 | 6 |
| PlayerState | 2 | 5 |
| FollowOrder | 1 | 4 |
| WorldBootOptions | 4 | 4 |
| EditCharacter | 2 | 4 |
| ProductionSettings | 4 | 4 |
| BuyItem | 2 | 4 |
| SellItem | 3 | 4 |
| DebugGiveItem | 2 | 3 |
| DebugPlaceItem | 2 | 3 |
| CombatSystem | 3 | 3 |
| SaveLoadOptions | 2 | 3 |
| AiSettings | 1 | 2 |
| PutLootItem | 2 | 2 |
| TakeLootItem | 2 | 2 |
| ConstructionSettings | 2 | 2 |

## Game files that write simulation state most

| File | Write sites |
|---|---|
| GameSaves.cs | 63 |
| ContextMenuWindow.cs | 60 |
| DebugMenu.cs | 45 |
| GameHost.cs | 38 |
| FurnitureUse.cs | 34 |
| AgentOrders.cs | 32 |
| AgentPlay.cs | 29 |
| ScreenshotScenes.cs | 28 |
| AgentWorld.cs | 24 |
| PlayerInterface.cs | 24 |
| PlayBenchScript.cs | 22 |
| BuildMode.cs | 19 |
| WorldMap.cs | 18 |
| BuildingSelection.cs | 15 |
| DebugCharactersWindow.cs | 13 |
| ProductionWindows.cs | 13 |
| WorldSession.cs | 13 |
| InventoryWindows.cs | 12 |
| SandboxScenario.cs | 12 |
| TradeWindows.cs | 9 |
| GameLoop.cs | 9 |
| CharacterEditorPanel.cs | 8 |
| SquadPanel.cs | 8 |
| DebugItemsWindow.cs | 7 |
| PlayerInterface.Ui.cs | 7 |

## Members written from four or more simulation slices

| Member | Slices | Write sites | Hashed | Saved |
|---|---|---|---|---|
| CharacterHot.Mode | 13 | 27 | 1 | 0 |
| CharacterHot.TaskTime | 12 | 54 | 1 | 0 |
| CharacterHot.Yaw | 11 | 40 | 1 | 1 |
| CharacterHot.Flags | 10 | 46 | 1 | 0 |
| PlayerState.Money | 10 | 23 | 1 | 1 |
| CharacterHot.Goal | 9 | 18 | 1 | 0 |
| CharacterHot.Position | 9 | 19 | 1 | 1 |
| CharacterHot.Task | 9 | 20 | 1 | 0 |
| CharacterHot.Velocity | 9 | 28 | 1 | 0 |
| CharacterCold.Engaged | 8 | 45 | 1 | 0 |
| CharacterCold.Orders | 8 | 11 | 1 | 1 |
| CharacterCold.PathRequest | 8 | 19 | 1 | 0 |
| CharacterCold.RecentlyWanted | 7 | 12 | 1 | 1 |
| CharacterHot.PathCursor | 7 | 21 | 1 | 0 |
| ItemInstance.Level | 7 | 12 | 1 | 1 |
| CharacterCold.Bounties | 6 | 10 | 1 | 1 |
| CharacterCold.DrawnWeapon | 6 | 21 | 1 | 0 |
| CharacterCold.Shaved | 6 | 7 | 1 | 1 |
| Squad.Money | 6 | 20 | 1 | 1 |
| ItemInstance.CompanyId | 6 | 8 | 0 | 1 |
| ItemInstance.MaterialId | 6 | 9 | 0 | 1 |
| CharacterCold.Ai | 5 | 14 | 1 | 0 |
| CharacterCold.Escaped | 5 | 8 | 1 | 1 |
| CharacterCold.FollowOrder | 5 | 9 | 1 | 0 |
| CharacterCold.Running | 5 | 10 | 1 | 0 |
| CharacterCold.SquadId | 5 | 15 | 1 | 1 |
| AiMind.Replan | 5 | 17 | 1 | 0 |
| Squad.Leader | 5 | 26 | 1 | 1 |
| ItemInstance.Mark | 5 | 23 | 0 | 1 |
| LockState.Locked | 5 | 25 | 1 | 1 |
| OutputSlot.Count | 5 | 16 | 1 | 1 |
| CharacterCold.Fleeing | 4 | 7 | 1 | 0 |
| CharacterCold.Freed | 4 | 7 | 1 | 1 |
| CharacterCold.HoldPoint | 4 | 6 | 0 | 0 |
| CharacterCold.KidnapVictim | 4 | 5 | 1 | 1 |
| CharacterCold.Ordered | 4 | 5 | 1 | 0 |
| CharacterCold.PathFailed | 4 | 9 | 0 | 0 |
| CharacterCold.Pending | 4 | 6 | 1 | 0 |
| CharacterCold.Role | 4 | 11 | 1 | 1 |
| CharacterCold.SlaveOwner | 4 | 6 | 1 | 1 |

## CharacterCold

218 members; changed after construction 201; hashed 107; saved 39; published 76; hashed and not saved 77.

