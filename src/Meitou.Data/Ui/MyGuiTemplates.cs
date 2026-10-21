using System.Globalization;

namespace Meitou.Data.Ui;

/// <summary>Resolve ResourceLayout skins and their Client regions without rewriting the source layout.</summary>
public static partial class MyGuiTemplates
{
    public static MyGuiLayout Expand(MyGuiLayout layout, MyGuiResources resources) => new(layout.Version,
        layout.Roots.Select(w => ExpandWidget(w, resources, [])).ToArray());

    static MyGuiWidget ExpandWidget(MyGuiWidget source, MyGuiResources resources, HashSet<string> chain)
    {
        if (source.Properties.GetValueOrDefault("ResolvedTemplate") == "true") return source;
        var children = source.Children.Select(c => ExpandWidget(c, resources, new(chain))).ToArray();
        if (!resources.Templates.TryGetValue(source.Skin, out var resource) && (!resources.Resources.TryGetValue(source.Skin, out resource) || MyGuiReader.Attribute(resource, "type") != "ResourceLayout"))
            return source with { Children = children };
        if (!chain.Add(source.Skin)) throw new InvalidDataException($"Cyclic MyGUI template: {source.Skin}");
        var template = MyGuiReader.ReadResourceLayout(resource).Roots.FirstOrDefault() ?? throw new InvalidDataException($"Empty MyGUI template: {source.Skin}");
        bool placed = false;
        int anonymous = 0;
        MyGuiWidget Clone(MyGuiWidget node, float referenceWidth, float referenceHeight)
        {
            var properties = new Dictionary<string, string>(node.Properties, StringComparer.Ordinal)
            {
                ["TemplateReferenceWidth"] = referenceWidth.ToString(CultureInfo.InvariantCulture),
                ["TemplateReferenceHeight"] = referenceHeight.ToString(CultureInfo.InvariantCulture),
                ["TemplateOwner"] = source.Name,
            };
            string name = source.Name + "@" + (node.Name.Length > 0 ? node.Name : "part") + "#" + anonymous++;
            string originalName = node.Name;
            properties["TemplatePart"] = originalName;
            if (originalName == "Caption") properties["CaptionOwner"] = source.Name;
            if (node.UserStrings.TryGetValue("Event", out string? eventName)) properties["TemplateEvent"] = eventName;
            var nested = node.Children.Select(c => Clone(c, node.Position.Width, node.Position.Height)).ToList();
            if (originalName == "Client" && !placed && source.Type != "TabControl")
            {
                nested.AddRange(children);
                placed = true;
                if (source.Type is "EditBox" or "TextBox") properties["CaptionOwner"] = source.Name;
            }
            return ExpandWidget(node with { Name = name, Properties = properties, Children = nested }, resources, new(chain));
        }
        var framed = template.Children.Select(c => Clone(c, template.Position.Width, template.Position.Height)).ToList();
        if (!placed) framed.AddRange(children);
        var rootProperties = new Dictionary<string, string>(template.Properties, StringComparer.Ordinal);
        foreach (var p in source.Properties) rootProperties[p.Key] = p.Value;
        if (framed.Any(c => c.DescendantsAndSelf().Any(n => n.Properties.ContainsKey("CaptionOwner")))) rootProperties["SkipCaption"] = "true";
        var strings = new Dictionary<string, string>(template.UserStrings, StringComparer.Ordinal);
        foreach (var p in source.UserStrings) strings[p.Key] = p.Value;
        if (source.Type == "MultiListBox") framed.AddRange(ColumnPrototypes(source.Name, strings, resources, chain));
        var expanded = source with { Skin = template.Skin, Properties = rootProperties, UserStrings = strings, Children = framed };
        // A template's root requests a native skin, even when that skin shares the template's name.
        var resolvedProperties = new Dictionary<string, string>(expanded.Properties, StringComparer.Ordinal) { ["ResolvedTemplate"] = "true" };
        return expanded with { Properties = resolvedProperties };
    }
}
