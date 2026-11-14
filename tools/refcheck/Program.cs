// Resolves every type/member reference a mod DLL makes into sts2.dll (and other game DLLs) against a game folder.
using Mono.Cecil;
var mod = args[0]; var gameDir = args[1];
var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(gameDir);
var m = ModuleDefinition.ReadModule(mod, new ReaderParameters { AssemblyResolver = resolver });
if (args.Length > 2 && args[2] == "--list")
{
    foreach (var r in m.GetMemberReferences().Where(r => r.DeclaringType.Scope.Name is "sts2" or "GodotSharp" or "0Harmony").Select(r => r.FullName).Distinct().OrderBy(x => x)) Console.WriteLine(r);
    foreach (var t in m.GetTypeReferences().Where(t => t.Scope.Name is "sts2").Select(t => "TYPE " + t.FullName).OrderBy(x => x)) Console.WriteLine(t);
    return 0;
}
int ok = 0, bad = 0;
foreach (var t in m.GetTypeReferences())
{
    if (t.Scope.Name.StartsWith("System") || t.Scope.Name is "mscorlib" or "netstandard") continue;
    try { if (t.Resolve() == null) { bad++; Console.WriteLine($"MISSING TYPE {t.FullName} ({t.Scope.Name})"); } else ok++; }
    catch (Exception e) { bad++; Console.WriteLine($"ERR TYPE {t.FullName}: {e.Message}"); }
}
foreach (var r in m.GetMemberReferences())
{
    var scope = r.DeclaringType.Scope.Name;
    if (r.DeclaringType.GetElementType().Scope is ModuleDefinition) continue;
    if (scope.StartsWith("System") || scope is "mscorlib" or "netstandard") continue;
    try
    {
        object res = r is MethodReference mr ? mr.Resolve() : r is FieldReference fr ? fr.Resolve() : null;
        if (res == null) { bad++; Console.WriteLine($"MISSING {r.FullName} ({scope})"); } else ok++;
    }
    catch (Exception e) { bad++; Console.WriteLine($"ERR {r.FullName}: {e.Message}"); }
}
Console.WriteLine($"resolved {ok}, missing {bad}");
return bad == 0 ? 0 : 1;
