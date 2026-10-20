namespace Meitou.Simulation;

public sealed partial class World
{
    /// <summary>
    /// Applies inventory, selection and open-shop transactions between ticks while paused, without advancing time or running simulation phases; also a host's
    /// <see cref="JoinPlayer"/> and <see cref="TransferSquad"/> (never queued in single-player), so a player joins without waiting for the unpause
    /// (docs/simulation.md "Per-viewer snapshots"). A replica throws: it never ticks.
    /// </summary>
    public void ApplyPausedInterfaceCommands()
    {
        if (IsReplica) throw ReplicaTicks();
        if (Characters.Phase != TablePhase.Outside) throw new InvalidOperationException("interface commands require a tick boundary");
        var due = Commands.TakeDue(Tick, c => c is SelectCommand or InspectCommand or InspectBuildingCommand or Items.MoveInventoryItem or Items.ArrangeInventory
            or Trading.BuyItem or Trading.SellItem or Trading.CloseTrade or Dialogue.DialogueChoice or Dialogue.EndConversation
            or Construction.PlaceBuildings or Construction.CancelSite or Construction.PauseSite or Editing.EditCharacter or Editing.FinishCreation
            or JoinPlayer or TransferSquad);
        if (due.Count == 0) return;
        stateStamp++;
        Characters.BeginTick();
        Characters.Phase = TablePhase.Inputs;
        try
        {
            foreach (var system in systems)
                if (system is PlayerSystem or Trading.TradeSystem or Construction.ConstructionSystem or Dialogue.DialogueSystem) system.Inputs(this, due);
            foreach (var population in systems.OfType<PopulationSystem>()) population.ApplyJoins(this, due);
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
}
