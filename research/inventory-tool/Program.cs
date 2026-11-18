// State inventory of the simulation: every field and auto-property declared in Meitou.Simulation, with who writes it, who reads it from the
// game host, and whether the world hash, the save and the published snapshot take it. Bound with the compiler's semantic model.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string repo = args[0];
string outFile = args[1];
string frameworkRefs = args[2];
string gameBin = Path.Combine(repo, "src", "Meitou.Game", "bin", "Debug", "net11.0");

var parse = new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.None, preprocessorSymbols: ["DEBUG", "TRACE", "NET", "NET11_0"]);
var framework = Directory.GetFiles(frameworkRefs, "*.dll").Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();

var simulation = Compile("Meitou.Simulation", "src/Meitou.Simulation", ["Meitou.Core", "Meitou.Data", "BepuPhysics", "BepuUtilities",
    "DotRecast.Core", "DotRecast.Recast"], []);
var game = Compile("MeitouClient", "src/Meitou.Game", ManagedIn(gameBin).Where(n => n is not ("MeitouClient" or "Meitou.Simulation")).ToArray(),
    [simulation.ToMetadataReference()]);

var members = new Dictionary<string, Member>(StringComparer.Ordinal);
CollectMembers(simulation);
Console.WriteLine($"state members declared in the simulation: {members.Count}");

Scan(simulation, fromGame: false);
Scan(game, fromGame: true);
Write();
return;

// ---------------------------------------------------------------------------------------------------------------------------------------

CSharpCompilation Compile(string name, string folder, string[] libraries, MetadataReference[] extra)
{
    var files = Git("ls-files", folder + "/*.cs").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    string usings = Path.Combine(repo, folder, "obj", "Debug", "net11.0", Path.GetFileName(folder) + ".GlobalUsings.g.cs");
    var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(repo, f)), parse, f)).ToList();
    trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(usings), parse, "GlobalUsings.g.cs"));

    var references = framework.Concat(libraries.Select(l => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(gameBin, l + ".dll"))))
        .Concat(extra);
    var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable);
    var compilation = CSharpCompilation.Create(name, trees, references, options);
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    var kinds = errors.GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key} x{g.Count()}");
    Console.WriteLine($"{name}: {files.Count} files, {errors.Count} binding errors ({string.Join(", ", kinds)})");
    return compilation;
}

// The managed assemblies of a folder, by name (a native library has no metadata).
IEnumerable<string> ManagedIn(string folder)
{
    foreach (string file in Directory.GetFiles(folder, "*.dll"))
    {
        bool managed;
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(file);
            managed = true;
        }
        catch (BadImageFormatException)
        {
            managed = false;
        }

        if (managed) yield return Path.GetFileNameWithoutExtension(file);
    }
}

void CollectMembers(CSharpCompilation compilation)
{
    foreach (var tree in compilation.SyntaxTrees)
    {
        var model = compilation.GetSemanticModel(tree);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field } variable when !field.Modifiers.Any(SyntaxKind.ConstKeyword):
                    Add(model.GetDeclaredSymbol(variable), "field", tree.FilePath);
                    break;
                case PropertyDeclarationSyntax property when IsAuto(property):
                    Add(model.GetDeclaredSymbol(property), "property", tree.FilePath);
                    break;
                case ParameterSyntax { Parent.Parent: RecordDeclarationSyntax } parameter:
                    var owner = model.GetDeclaredSymbol((RecordDeclarationSyntax)parameter.Parent!.Parent!);
                    Add(owner?.GetMembers(parameter.Identifier.ValueText).OfType<IPropertySymbol>().FirstOrDefault(), "record property", tree.FilePath);
                    break;
            }
        }
    }
}

bool IsAuto(PropertyDeclarationSyntax property) =>
    property.ExpressionBody is null && property.AccessorList is { } list && list.Accessors.All(a => a.Body is null && a.ExpressionBody is null)
    && !property.Modifiers.Any(SyntaxKind.AbstractKeyword) && property.Parent is not InterfaceDeclarationSyntax;

void Add(ISymbol? symbol, string kind, string file)
{
    if (symbol is null || symbol.ContainingType is null || symbol.GetDocumentationCommentId() is not { } id || members.ContainsKey(id)) return;

    var type = symbol.ContainingType;
    members[id] = new Member
    {
        Type = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        Namespace = type.ContainingNamespace.ToDisplayString(),
        Name = symbol.Name,
        Kind = kind,
        DataType = (symbol as IFieldSymbol)?.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            ?? ((IPropertySymbol)symbol).Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
        Static = symbol.IsStatic,
        Public = symbol.DeclaredAccessibility == Accessibility.Public && IsPublic(type),
        ValueType = type.IsValueType,
        File = file,
    };
}

bool IsPublic(INamedTypeSymbol type)
{
    for (var t = type; t is not null; t = t.ContainingType)
        if (t.DeclaredAccessibility != Accessibility.Public) return false;

    return true;
}

void Scan(CSharpCompilation compilation, bool fromGame)
{
    var watch = Stopwatch.StartNew();
    foreach (var tree in compilation.SyntaxTrees)
    {
        var model = compilation.GetSemanticModel(tree);
        foreach (var name in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (symbol is not (IFieldSymbol or IPropertySymbol)) continue;
            if (symbol.ContainingAssembly?.Name != "Meitou.Simulation") continue;
            if (symbol.OriginalDefinition.GetDocumentationCommentId() is not { } id || !members.TryGetValue(id, out var member)) continue;

            Note(member, name, model, tree.FilePath, fromGame);
        }
    }

    Console.WriteLine($"scanned {compilation.AssemblyName} in {watch.Elapsed.TotalSeconds:0} s");
}

void Note(Member member, IdentifierNameSyntax name, SemanticModel model, string file, bool fromGame)
{
    ExpressionSyntax access = name.Parent switch
    {
        MemberAccessExpressionSyntax m when m.Name == name => m,
        MemberBindingExpressionSyntax b => b,
        _ => name,
    };
    var use = UseOf(access);
    var context = ContextOf(name, model, file);

    if (fromGame)
    {
        if (use == Use.Read) Count(member.GameReads, Path.GetFileName(file));
        else if (use != Use.Init || !context.Constructs) Count(member.GameWrites, Path.GetFileName(file));
        return;
    }

    bool initial = use == Use.Init || context.Constructs && context.OwnType == member.Type;
    if (use == Use.Read)
    {
        Count(member.Readers, context.Slice);
        if (context.Hash) member.Hashed = true;
        if (context.Save) member.Saved = true;
        if (context.Publish) member.Published = true;
        return;
    }

    if (context.Save) member.Loaded = true;
    if (initial) member.InitWrites++;
    else if (use == Use.Content) Count(member.ContentWriters, context.Slice);
    else Count(member.Writers, context.Slice);

    // A compound assignment or an increment reads too.
    if (use == Use.ReadWrite && context.Hash) member.Hashed = true;
}

Use UseOf(ExpressionSyntax access)
{
    SyntaxNode? parent = access.Parent;
    while (parent is ParenthesizedExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression })
        parent = parent.Parent;

    switch (parent)
    {
        case AssignmentExpressionSyntax assignment when assignment.Left.DescendantNodesAndSelf().Contains(access) && IsTarget(assignment.Left, access):
            if (assignment.Parent is InitializerExpressionSyntax) return Use.Init;

            return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? Use.Write : Use.ReadWrite;
        case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
        case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
            return Use.ReadWrite;
        case ArgumentSyntax argument when argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword):
            return Use.Write;
        case RefExpressionSyntax:
            return Use.Write;
        case ElementAccessExpressionSyntax element when element.Expression == access && IsAssigned(element):
            return Use.Content;
        case MemberAccessExpressionSyntax call when call.Expression == access && call.Parent is InvocationExpressionSyntax && Mutators.Contains(call.Name.Identifier.ValueText):
            return Use.Content;
        default:
            return Use.Read;
    }
}

// Whether the access is what the assignment stores into (not an index or a receiver inside its left side).
bool IsTarget(ExpressionSyntax left, ExpressionSyntax access)
{
    ExpressionSyntax target = left;
    while (target is ParenthesizedExpressionSyntax p) target = p.Expression;

    if (target == access) return true;
    if (target is TupleExpressionSyntax tuple) return tuple.Arguments.Any(a => IsTarget(a.Expression, access));

    return false;
}

bool IsAssigned(ExpressionSyntax element)
{
    SyntaxNode? parent = element.Parent;
    return parent is AssignmentExpressionSyntax a && a.Left == element
        || parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax { RawKind: not (int)SyntaxKind.SuppressNullableWarningExpression };
}

Context ContextOf(SyntaxNode node, SemanticModel model, string file)
{
    var method = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
    var symbol = method is null ? null : model.GetDeclaredSymbol(method);
    var typeNode = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
    var type = typeNode is null ? null : model.GetDeclaredSymbol(typeNode);
    string methodName = symbol?.Name ?? "";
    string fileName = Path.GetFileName(file);
    string ns = type?.ContainingNamespace.ToDisplayString() ?? "";

    bool hash = symbol is not null && (symbol.Parameters.Any(p => p.Type.Name == "StateHasher") || methodName.Contains("Hash", StringComparison.Ordinal))
        || (type?.Name.Contains("Hash", StringComparison.Ordinal) ?? false);
    bool save = file.Contains("/Saving/", StringComparison.Ordinal) || fileName.EndsWith("Save.cs", StringComparison.Ordinal);
    bool publish = fileName.StartsWith("WorldPublish", StringComparison.Ordinal) || methodName.StartsWith("Publish", StringComparison.Ordinal);
    bool constructs = method is ConstructorDeclarationSyntax || node.Ancestors().Any(a => a is FieldDeclarationSyntax or PropertyDeclarationSyntax
        && node.Ancestors().OfType<EqualsValueClauseSyntax>().Any());

    return new Context(ns.StartsWith("Meitou.Simulation", StringComparison.Ordinal) ? ns["Meitou.".Length..] : ns,
        type?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "", hash, save, publish, constructs);
}

void Count(SortedDictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

void Write()
{
    var rows = members.Values.OrderBy(m => m.Namespace, StringComparer.Ordinal).ThenBy(m => m.Type, StringComparer.Ordinal)
        .ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
    File.WriteAllText(outFile, JsonSerializer.Serialize(rows, new JsonSerializerOptions { IncludeFields = true }));
    Console.WriteLine($"wrote {rows.Count} members to {outFile}");
}

string Git(params string[] arguments)
{
    var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, WorkingDirectory = repo };
    foreach (string argument in arguments) info.ArgumentList.Add(argument);

    using var process = Process.Start(info)!;
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output;
}

enum Use { Read, Write, ReadWrite, Init, Content }

record Context(string Slice, string OwnType, bool Hash, bool Save, bool Publish, bool Constructs);

sealed class Member
{
    public string Type = "", Namespace = "", Name = "", Kind = "", DataType = "", File = "";
    public bool Static, Public, ValueType, Hashed, Saved, Loaded, Published;
    public int InitWrites;
    public SortedDictionary<string, int> Writers = [], ContentWriters = [], Readers = [], GameReads = [], GameWrites = [];
}

static partial class Program
{
    static readonly HashSet<string> Mutators =
    [
        "Add", "AddRange", "Remove", "RemoveAt", "RemoveAll", "RemoveRange", "Clear", "Insert", "Enqueue", "Dequeue", "TryDequeue", "Push", "Pop",
        "TryPop", "TryAdd", "TryRemove", "Sort", "Reverse", "Set", "SetLength", "EnsureCapacity", "TrimExcess", "UnionWith", "ExceptWith",
        "IntersectWith", "Fill", "CopyTo", "AddOrUpdate", "GetOrAdd", "Swap", "Resize",
    ];
}
