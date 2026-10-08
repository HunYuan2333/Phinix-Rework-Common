using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Utils.Framework;

internal static class Program
{
    private static int assertions;
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static Assembly Resolve(IDisposable scope, string name, Assembly requester)
    {
        return (Assembly)scope.GetType().GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(scope, new object[] { null, new ResolveEventArgs(name, requester) });
    }
    private static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "phinix-owned-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fixtures = Path.GetFullPath(args[0]);
            string owned = Path.Combine(root, "owned"), foreign = Path.Combine(root, "foreign"), duplicates = Path.Combine(root, "duplicates");
            Directory.CreateDirectory(owned); Directory.CreateDirectory(foreign); Directory.CreateDirectory(duplicates);
            File.Copy(Path.Combine(fixtures, "Fixture.Managed.Helper.dll"), Path.Combine(owned, "01-not-the-identity.dll"));
            File.Copy(Path.Combine(fixtures, "Fixture.Managed.Plugin.dll"), Path.Combine(owned, "02-module.dll"));
            File.Copy(Path.Combine(fixtures, "Fixture.Managed.Provider.dll"), Path.Combine(foreign, "Fixture.Managed.Provider.dll"));
            var warnings = new List<string>();
            var scope = ExtensionAssemblyLoader.LoadOwnedAssemblies(new[] { owned, owned }, (message, level) => warnings.Add(message));
            var helper = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Fixture.Managed.Helper");
            var module = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Fixture.Managed.Plugin");
            Check(warnings.Count == 0, "The prefixed declared directory loads its complete graph without errors.");
            Check(module.GetTypes().Any(t => typeof(IPhinixExtensionModule).IsAssignableFrom(t)), "Owned modules remain discoverable by the ordinary framework contract.");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Fixture.Managed.Provider"), "An unlisted ordinary mod directory is never scanned or loaded.");
            Check(Resolve(scope, helper.FullName, typeof(Program).Assembly) == null, "A foreign request cannot borrow the owned resolver.");
            Check(Resolve(scope, helper.FullName, null) == null, "A request without source ownership is not resolved.");
            Check(ReferenceEquals(Resolve(scope, helper.FullName, module), helper), "An owned request resolves an exactly declared dependency.");
            var wrong = new AssemblyName(helper.FullName); wrong.Version = new Version(99, 0, 0, 0);
            Check(Resolve(scope, wrong.FullName, module) == null, "A mismatching version cannot resolve by simple name.");
            Check(Resolve(scope, typeof(Program).Assembly.FullName, module) == null, "An undeclared request is rejected.");
            File.Copy(Path.Combine(fixtures, "Fixture.Managed.Provider.dll"), Path.Combine(owned, "late.dll"));
            Check(Resolve(scope, AssemblyName.GetAssemblyName(Path.Combine(owned, "late.dll")).FullName, module) == null,
                "Files added after preparation are not directory-probed.");
            scope.Dispose(); scope.Dispose();
            Check(Resolve(scope, helper.FullName, module) == null, "Disposed scopes stop resolution and disposal is idempotent.");
            // Name ambiguities must not select whichever file happened to enumerate last.
            File.Copy(Path.Combine(fixtures, "Fixture.Managed.Provider.dll"), Path.Combine(duplicates, "first.dll"));
            File.Copy(Path.Combine(fixtures, "Fixture.Managed.Provider.dll"), Path.Combine(duplicates, "second.dll"));
            warnings.Clear();
            using (ExtensionAssemblyLoader.LoadOwnedAssemblies(new[] { duplicates }, (message, level) => warnings.Add(message)))
            {
                Check(warnings.Any(w => w.Contains("Ambiguous owned assembly name")), "Duplicate CLR names produce an explicit diagnostic.");
                Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Fixture.Managed.Provider"), "Ambiguous candidates are not executed.");
            }
            var gameLoaded = Assembly.LoadFrom(Path.Combine(foreign, "Fixture.Managed.Provider.dll"));
            Check(PhinixExtensionRegistry.ScanCandidateModuleTypes().Any(t => t.Assembly == gameLoaded),
                "An ordinary submod already loaded by the game remains discoverable through the same registry.");
            Console.WriteLine("Owned extension loader passed: " + assertions + " assertions.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
