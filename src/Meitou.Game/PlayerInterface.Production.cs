using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Items;
using Meitou.Simulation.Production;
using Meitou.Ui;

namespace Meitou.Game;

/// <summary>
/// Production in the UI (docs/simulation.md "Production", docs/game/ui-screens.md "Selected building"):
/// <list type="bullet">
/// <item>Selecting a complete machine of the player's opens its window beside the inventory: a well or farm the original's <c>Kenshi_GenericBuildingWindow</c> (inputs in <c>in1</c> / <c>in2</c>,
/// the output in <c>out</c>, their bars and texts), a research bench <c>Kenshi_InventoryResearchWindow</c> (its storage grid; the RESEARCH button opens the overview's research tab).
/// Items are held with the mouse between the windows and the selected character's inventory (PlayerInterface.Inventory.cs), or moved with a right click (<see cref="PutInMachine"/>,
/// <see cref="TakeFromMachine"/>).</item>
/// <item>The site menu has Operate for a machine characters work; a quick right click on one gives it.</item>
/// <item>The overview's research tab: TYPES lists the categories, BLUEPRINTS the research of the selected category that can be queued (a click queues it), QUEUE the queue (a click
/// takes one off), DESCRIPTION the hovered or last clicked research. The original's row widgets and colours are <b>Unknown</b>; Meitou draws plain text rows.</item>
/// </list>
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>The site whose machine window is shown (null: none), and a site whose window the player closed (not reopened until the selection changes).</summary>
    string? shownMachine, closedMachine;
    string? researchCategory, researchFocus;

    MachineSnapshot? Machine(string? site) => site is null ? null : session.CurrentSnapshot.Machines.FirstOrDefault(m => m.Site == site);

    /// <summary>The storage window's layout pixels above its grid (caption and item heading) and in all around it, with the two-line Max Capacity text below (measured on the original's window).</summary>
    const float StorageAbove = 86, StorageChrome = 150;

    static string MachineScreen(MachineSnapshot m) => m.Storage is null || m.Inputs.Count > 0 || m.Output is not null ? "building" : m.IsStorage && !m.IsContainer && m.TechLevel == 0 ? "storage" : "bench";

    /// <summary>Opens the selected machine's window and closes it when the selection moves on.</summary>
    void SyncMachineScreens()
    {
        if (faithfulScreens is null) return;
        var focus = BuildingFocus();
        // A rock's natural site is no registry building: its machine, once mined, is enough.
        var m = focus?.SiteId is { } id && (ProductionSystem.IsNatural(id) || session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == id) is { Complete: true }) ? Machine(id) : null;
        if (m is not null && m.Inputs.Count == 0 && m.Output is null && m.Storage is null && m.Craft is null) m = null;
        // An open container takes the window.
        m = session.CurrentSnapshot.Machines.FirstOrDefault(c => c.IsContainer) ?? m;
        if (closedMachine is not null && closedMachine != m?.Site) closedMachine = null;
        if (shownMachine is not null && (m is null || m.Site != shownMachine))
        {
            faithfulScreens.Close("building");
            faithfulScreens.Close("bench");
            faithfulScreens.Close("storage");
            shownMachine = null;
            storageWithInventory = null;
        }
        if (shownMachine is not null && Machine(shownMachine) is { } shown && !faithfulScreens.IsOpen(MachineScreen(shown)))
        {
            if (shown.IsContainer) Say(new Meitou.Simulation.Items.CloseContainer { Tick = session.World.Tick });
            closedMachine = shownMachine;
            shownMachine = null;
            return;
        }
        if (m is null || shownMachine == m.Site || closedMachine == m.Site) return;
        shownMachine = m.Site;
        string screen = MachineScreen(m);
        inventoryScroll.Remove(screen);
        // The character's inventory opens with an order-driven container window (the original's order 26 opens the looter's own window and the target's together) and with a machine's; selecting
        // a storage or a mined rock shows its window alone (the original's Storage window of a selected ore storage has no inventory beside it).
        bool alone = !m.IsContainer && (m.IsStorage || ProductionSystem.IsNatural(m.Site)) && storageWithInventory != m.Site;
        if (!alone && Selected().Count > 0 && !faithfulScreens.IsOpen("inventory")) faithfulScreens.Open("inventory");
        faithfulScreens.Open(screen);
        PlaceInventoryBeside(screen, left: false);
    }

    void BindMachineScreen(string id, UiBindings b)
    {
        var m = Machine(shownMachine);
        if (id == "storage")
        {
            // The original's generic container window: caption STORAGE, the limited item as heading, and Max Capacity (red "(Full)" once the stack is full).
            b.Text["Root"] = "STORAGE";
            b.Text["TypeText"] = m?.StorageLimitName ?? "";
            if (m is { StorageCapacity: > 0 } cap)
            {
                bool full = m.Storage?.Items.FirstOrDefault()?.Quantity >= cap.StorageCapacity;
                b.Text["CapacityText"] = $"Max Capacity: {cap.StorageCapacity.ToString("N0", CultureInfo.InvariantCulture)}" + (full ? " (Full)" : "");
                // The original paints the whole line red once the stack is full (the exact red is Unknown; read off its screenshot).
                if (full) b.Properties["CapacityText"] = new Dictionary<string, string> { ["TextColour"] = "0.91 0.25 0.25" };
            }
            else b.Hidden.Add("CapacityText");
            b.Disabled.Remove("ArrangeButton");
            b.Hidden.Add("ArrangeButton");
            if (m?.Storage is { } sg)
                b.Positions["Root"] = new(0, 0, sg.Width * InventoryCell + 40 * InventoryScale, Math.Min(Height * 0.8f, sg.Height * InventoryCell + StorageChrome * InventoryScale));
            b.TemplateScale = InventoryScale;
            return;
        }
        b.Text["Root"] = (m is { IsContainer: true } ? buildHost?.ItemName(m.Record) ?? "CONTAINER" : selectedBuilding?.Name ?? "BUILDING").ToUpperInvariant();
        if (id == "bench")
        {
            b.Disabled.Remove("ResearchButton");
            b.Actions["ResearchButton"] = "ResearchButton";
            if (m is not { TechLevel: > 0 }) b.Hidden.Add("ResearchButton");
            b.Disabled.Remove("ArrangeButton");
            b.Hidden.Add("ArrangeButton");
            if (m?.Storage is { } grid)
                b.Positions["Root"] = new(0, 0, grid.Width * InventoryCell + 40 * InventoryScale, Math.Min(Height * 0.8f, grid.Height * InventoryCell + 108 * InventoryScale));
            b.TemplateScale = InventoryScale;
            return;
        }
        if (m?.Craft is { } craft)
        {
            b.Text["CraftingName"] = craft.Queue is [var (_, current, progress, _), ..] ? $"{current}  {(int)(progress * 100)}%" : "Nothing queued";
            b.Disabled.Remove("QueueButton");
            b.Actions["QueueButton"] = "QueueButton";
        }
        else b.Hidden.Add("CraftingPanel");
        if (m is null) return;
        for (int k = 0; k < 2; k++)
        {
            string n = (k + 1).ToString(CultureInfo.InvariantCulture);
            if (k >= m.Inputs.Count) { b.Hidden.Add($"Input{n}Panel"); continue; }
            var input = m.Inputs[k];
            b.Text[$"Input{n}Text"] = input.Name;
            b.Text[$"Input{n}StatusText"] = $"{input.Count.ToString(CultureInfo.InvariantCulture)}/{input.Capacity.ToString(CultureInfo.InvariantCulture)}";
            b.Progress[$"Input{n}ProgressBar"] = input.Fill;
        }
        if (m.Output is { } output)
        {
            b.Text["OutputText"] = $"{output.Name}\n{output.Count.ToString(CultureInfo.InvariantCulture)}/{output.Capacity.ToString(CultureInfo.InvariantCulture)}";
            b.Progress["OutputProgressBar"] = m.Farm is { } farm ? farm.Ripeness : output.Fill;
        }
        else if (m.Craft is { } done)
        {
            b.Text["OutputText"] = done.Done is [var first, ..] ? $"{first.Name}\n{done.Done.Sum(d => d.Quantity).ToString(CultureInfo.InvariantCulture)}" : "";
            b.Progress["OutputProgressBar"] = done.Queue is [var (_, _, p, _), ..] ? p : 0;
        }
        else b.Hidden.Add("OutputColumn");
        if (m.Inputs.Count == 0) b.Hidden.Add("InputTitleText");
    }

    void AddMachineContent(string id, List<UiControl> controls)
    {
        if (Machine(shownMachine) is not { } m) return;
        if (id is "bench" or "storage")
        {
            if (m.Storage is not { } grid) return;
            if (id == "storage") ArrangeContainerGrid(id, controls, grid.Width, grid.Height, StorageAbove, StorageChrome - StorageAbove - 6);
            else ArrangeContainerGrid(id, controls, grid.Width, grid.Height);
            if (controls.FirstOrDefault(c => c.Name == "backpack_content") is not { } slot) return;
            var area = slot.Bounds;
            foreach (var item in grid.Items)
            {
                if (Lifted(ItemOwner.AtSite(m.Site), 1000 + item.Index)) continue;
                AddItemCell(controls, $"machine-item-{item.Index}",
                    new(area.X + item.X * area.Width / grid.Width, area.Y + item.Y * area.Height / grid.Height, item.Width * area.Width / grid.Width, item.Height * area.Height / grid.Height),
                    item.Record, item.Material, item.Name, item.Quantity, ItemTip.Of(item), "MachineItem", 1000 + item.Index,
                    slot.Bounds.Intersect(slot.Clip ?? slot.Bounds), charge: item.Charge);
            }
            return;
        }
        void Slot(string name, MachineSlotView view, int code)
        {
            if (view.Count <= 0 || controls.FirstOrDefault(c => c.Name == name) is not { } slot || Lifted(ItemOwner.AtSite(m.Site), code)) return;
            var b = slot.Bounds;
            // The item at its own footprint, as the original draws it in a slot (a box of building material is 6 x 3 cells), centred in the slot.
            float cell = InventoryCell, w = view.Width * cell, h = view.Height * cell;
            AddItemCell(controls, $"machine-{name}", new(b.X + (b.Width - w) / 2, b.Y + (b.Height - h) / 2, w, h), view.Item, "", view.Name, view.Count, null, "MachineItem", code, slot.Clip);
        }
        for (int k = 0; k < Math.Min(2, m.Inputs.Count); k++) Slot($"in{k + 1}", m.Inputs[k], 10 + k);
        if (m.Output is { } output) Slot("out", output, 8);
        else if (m.Craft is { Done: [var made, ..] } && controls.FirstOrDefault(c => c.Name == "out") is { } outSlot && !Lifted(ItemOwner.AtSite(m.Site), 8))
        {
            var ob = outSlot.Bounds;
            float side = MathF.Min(ob.Width, ob.Height);
            AddItemCell(controls, "machine-out", new(ob.X, ob.Y, side, side), made.Record, made.Material, made.Name, made.Quantity, null, "MachineItem", 8, outSlot.Clip);
        }
    }

    void MachineAction(MyGuiEvent e)
    {
        if (e.Screen == "bench" && e.Widget == "ResearchButton") { faithfulScreens.Open("overview"); faithfulScreens.SetTab("overview", "TabsMain", 2); }
        else if (e.Screen == "building" && e.Widget == "QueueButton" && shownMachine is not null) OpenCraftTab(shownMachine);
    }

    /// <summary>The verb for a complete machine characters work (the original's verb names for wells, farms and benches are <b>Unknown</b>; Meitou says Operate).</summary>
    void AddMachineVerb(List<(string Caption, Action Give)> verbs, Meitou.Simulation.Construction.BuildingSnapshot site, IReadOnlyList<CharacterId> selection)
    {
        if (site.Complete && Machine(site.Id) is { Operable: true }) verbs.Insert(0, ("Operate", () => Say(new OperateOrder(selection, site.Id) { Tick = session.World.Tick })));
    }

    /// <summary>The stats lines of a selected machine: the farm's crop and growth, the output's progress, workers and the bench's tech level.</summary>
    void AddMachineLines(MachineSnapshot m, List<HudLine> stats, ref string status, ref bool bad)
    {
        string N(int v) => v.ToString(CultureInfo.InvariantCulture);
        string P(float v) => ((int)(Math.Clamp(v, 0, 1) * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        if (m.Farm is { } f)
        {
            status = f.Status switch { FarmStatus.NoWater => "No water", FarmStatus.OutputFull => "Output full", FarmStatus.Harvest => "Ready to harvest", _ => "Growing" };
            bad = f.Status is FarmStatus.NoWater or FarmStatus.OutputFull;
            stats.Add(new("Growth:", P(MathF.Min(f.Growth, 1)), MainColour));
            if (f.Growth >= 1) stats.Add(new("Ripeness:", P(f.Ripeness), MainColour, f.Growth >= 2 ? BadColour : GoodColour));
            stats.Add(new("Fertility:", P(f.Fertility), MainColour));
            if (f.Death > 0) stats.Add(new("Dying:", P(f.Death), MainColour, BadColour));
        }
        else if (m.Output is { } o)
        {
            status = m.Status switch { 2 => "Working", 1 => "Output full", _ => "Idle" };
            bad = m.Status == 1;
            stats.Add(new("Output:", $"{o.Name} {N(o.Count)}/{N(o.Capacity)}", MainColour));
        }
        // Power, in the original's words where it has them (GeneratorBuilding vfunc_114, vfunc_153); "No power" and "Low power" are Meitou's.
        if (m.Power is { } pw)
        {
            string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);
            if (pw.Kind == PowerKind.Battery)
            {
                status = pw.Granted > 0 ? "Charging" : pw.Granted < 0 ? "Draining" : pw.Charge <= 0 ? "Empty" : "Stored";
                bad = pw.Charge <= 0;
                stats.Add(new("Charge:", P(pw.Store > 0 ? pw.Charge / pw.Store : 0), MainColour));
            }
            else if (pw.Kind == PowerKind.Generator)
            {
                status = pw.Fueled ? "Running" : "Out of fuel";
                bad = !pw.Fueled;
                stats.Add(new("Power:", F(pw.Demand), MainColour));
                if (m.Inputs.Count > 0) stats.Add(new("Fuel Consumption", P(pw.Usage), MainColour));
            }
            else if (pw.Demand > 0)
            {
                if (pw.Granted < pw.Demand) { status = pw.Granted <= 0 ? "No power" : "Low power"; bad = true; }
                stats.Add(new("Power:", $"{F(pw.Granted)}/{F(pw.Demand)}", MainColour, pw.Granted < pw.Demand ? BadColour : null));
            }
        }
        if (m.TechLevel > 0) stats.Add(new("Tech level:", N(m.TechLevel), MainColour));
        if (m.MaxOperators > 0) stats.Add(new("Workers:", $"{N(m.Operators)}/{N(m.MaxOperators)}", MainColour));
    }

    // ---- the overview's research tab ----

    void BindResearchTab(UiBindings b)
    {
        var research = session.CurrentSnapshot.Research;
        var categories = research.Available.Select(r => r.Category).Distinct(StringComparer.Ordinal).ToList();
        if (researchCategory is null || !categories.Contains(researchCategory)) researchCategory = categories.FirstOrDefault();
        b.Lists["ResearchTypeList"] = [.. categories.Select(c => new UiListItem(c, c))];
        b.SelectedItems["ResearchTypeList"] = researchCategory is null ? -1 : categories.IndexOf(researchCategory);
        string message = research.BenchLevel <= 0 ? "Research needs a research bench." : research.Queue.Count == 0 ? "Nothing being researched." : "";
        if (message.Length > 0) b.Text["ResearchBenchMessage"] = message;
        else b.Hidden.Add("ResearchBenchMessage");
    }

    void ResearchAction(MyGuiEvent e)
    {
        if (e.Widget == "ResearchTypeList" && e.Action == "select") researchCategory = e.Text;
        else if (e.Action.StartsWith("research-queue:", StringComparison.Ordinal))
        {
            researchFocus = e.Action[15..];
            Say(new QueueResearch(researchFocus) { Tick = session.World.Tick });
        }
        else if (e.Action.StartsWith("research-cancel:", StringComparison.Ordinal)) Say(new CancelResearch(e.Action[16..]) { Tick = session.World.Tick });
    }

    /// <summary>The blueprint rows, the queue rows and the description, drawn into the research tab's panels.</summary>
    void AddResearchContent(List<UiControl> controls)
    {
        if (faithfulScreens.GetTab("overview", "TabsMain") != 2) return;
        var research = session.CurrentSnapshot.Research;
        var face = new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Left VCenter" };
        float line = MathF.Max(18, 24 * InventoryScale);
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);
        if (controls.FirstOrDefault(c => c.Name == "BlueprintsAvailablePanel") is { } blueprints)
        {
            var rows = research.Available.Where(r => r.Category == researchCategory).OrderBy(r => r.Level).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                bool queued = research.Queue.Any(q => q.Id == r.Id);
                string caption = $"{r.Name}  ({Loc.Tr("lvl {1,num}", r.Level)})" + (r.Progress > 0 ? $"  {(int)(r.Progress * 100)}%" : "");
                var colour = queued ? new Vector4(0.6f, 0.6f, 0.6f, 1) : r.BenchTooLow || !r.Affordable ? new Vector4(0.75f, 0.35f, 0.3f, 1) : ink;
                controls.Add(new($"research-row-{i}", "Button", new(blueprints.Bounds.X + 6, blueprints.Bounds.Y + i * line, blueprints.Bounds.Width - 12, line), caption,
                    queued ? "" : "research-queue:" + r.Id, "", !queued, face, Vector4.Zero, TextColour: colour, HoverColour: new(0, 1, 0, 1), Pickable: true, Clip: blueprints.Bounds));
            }
        }
        if (controls.FirstOrDefault(c => c.Name == "ResearchQueueList") is { } queue)
            for (int i = 0; i < research.Queue.Count; i++)
            {
                var (qid, name, progress) = research.Queue[i];
                controls.Add(new($"research-queue-{i}", "Button", new(queue.Bounds.X + 6, queue.Bounds.Y + i * line, queue.Bounds.Width - 12, line),
                    $"{name}  {(int)(progress * 100)}%", "research-cancel:" + qid, "", true, face, Vector4.Zero, TextColour: i == 0 ? new(0.66f, 0.72f, 0.45f, 1) : ink,
                    HoverColour: new(1, 0.4f, 0.3f, 1), Pickable: true, Clip: queue.Bounds));
            }
        var hovered = controls.LastOrDefault(c => c.Name.StartsWith("research-row-", StringComparison.Ordinal) && c.Bounds.Contains(mouse.X, mouse.Y))?.Action;
        string? focus = hovered is { Length: > 15 } h ? h[15..] : researchFocus;
        if (focus is null || research.Available.FirstOrDefault(r => r.Id == focus) is not { Id: not null } option) return;
        var lines = new List<HudLine>
        {
            new(option.Name, ""),
            new("Level:", option.Level.ToString(CultureInfo.InvariantCulture), MainColour, option.BenchTooLow ? BadColour : null),
            new("Time:", $"{option.Hours.ToString("0.#", CultureInfo.InvariantCulture)} h", MainColour),
        };
        foreach (var (item, count) in option.Cost) lines.Add(new(item, count.ToString(CultureInfo.InvariantCulture), MainColour, option.Affordable ? null : BadColour));
        AddLines(controls, "ResearchDescriptionPanel1", lines, line);
        if (option.Description.Length > 0 && controls.FirstOrDefault(c => c.Name == "ResearchDescriptionPanel2") is { } text)
            controls.Add(new("research-description", "EditBox", text.Bounds, option.Description, "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["WordWrap"] = "true", ["MultiLine"] = "true", ["Static"] = "true", ["TextAlign"] = "Left Top" },
                Vector4.Zero, TextColour: ink, Pickable: false, Clip: text.Bounds));
    }
}
