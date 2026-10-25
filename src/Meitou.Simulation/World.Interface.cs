namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>Applies player interface commands between ticks while paused, without advancing time or running simulation phases.</summary>
    public void ApplyPausedInterfaceCommands()
    {
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("interface commands require a tick boundary");
        var due = Commands.TakeDue(Tick, c => c is SelectCommand or InspectCommand or InspectBuildingCommand or ShowInventoryCommand or Items.HeldCommand or Items.QuickMoveItem or Items.ReadMapItem or Items.ArrangeInventory
            or Carrying.TakeLootItem or Carrying.PutLootItem or Production.PutInMachine or Production.TakeFromMachine
            or Trading.BuyItem or Trading.SellItem or Trading.ArrangeStock or Trading.CloseTrade or Dialogue.DialogueChoice or Dialogue.EndConversation or Dialogue.ConfirmCharacterTrade or Dialogue.CloseCharacterTrade
            or Construction.PlaceBuildings or Construction.CancelSite or Construction.PauseSite or Editing.EditCharacter or Editing.FinishCreation
            or SquadCommand or RenameFactionCommand or MoveOrder or FollowOrder or StopCommand or OrdersCommand or RunSpeedCommand or SquadAiCommand
            or FirstAid.TreatOrder or FirstAid.SplintOrder or FirstAid.RestOrder
            or Combat.AttackOrder or Combat.ShootAtOrder
            or Production.QueueResearch or Production.CancelResearch or Production.QueueCraft or Production.CancelCraft or Production.RepeatCrafts
            or Locks.PickLockOrder or Locks.GetOutOrder or Locks.ShackleOrder or Locks.PutShacklesOrder or Locks.ToggleDoorsOrder or Locks.ToggleDoorLocksOrder
            or Locks.DoorOrder or Locks.SetFreeOrder or Locks.GetInCageOrder);
        if (due.Count == 0) return;
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            foreach (var system in systems)
                if (system is PlayerSystem or MovementSystem or FirstAid.FirstAidSystem or Combat.CombatSystem or Locks.LockSystem
                    or Carrying.CarrySystem or Production.ProductionSystem or Trading.TradeSystem or Construction.ConstructionSystem or Dialogue.DialogueSystem)
                    system.Inputs(this, due);
        }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
        foreach (var trade in systems.OfType<Trading.TradeSystem>()) trade.RefreshView(this);
        foreach (var talk in systems.OfType<Dialogue.DialogueSystem>()) talk.RefreshView(this);
        if (Settings.PublishSnapshots)
        {
            var previous = PreviousSnapshot;
            Publish();
            PreviousSnapshot = previous;
        }
    }

    /// <summary>The first system of type <typeparamref name="T"/> this world runs, or null.</summary>
    internal T? SystemOf<T>() where T : class => systems.OfType<T>().FirstOrDefault();

    /// <summary>
    /// Puts the item on the mouse back where it came from (<see cref="PlayerState.Held"/>, <see cref="PlayerSystem.ReturnHeldItem"/>): a save does this first, so the item is saved in its
    /// window. Only at a tick boundary; nothing when none is held.
    /// </summary>
    public void ReturnHeldItem()
    {
        if (Player.Held is null || Characters.Phase != TablePhase.Outside || SystemOf<PlayerSystem>() is not { } players) return;
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try { players.ReturnHeldItem(this); }
        finally
        {
            Characters.Phase = TablePhase.Outside;
            Characters.EndTick();
        }
    }
}
