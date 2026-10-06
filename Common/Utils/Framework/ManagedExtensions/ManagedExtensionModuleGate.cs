using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    internal static class ManagedExtensionModuleGate
    {
        internal static Dictionary<string,string> Check(ManagedExtensionHostFacts host,
            IEnumerable<ManagedExtensionPackageSnapshot> candidates, HashSet<string> disabled, CancellationToken token)
        {
            var packages=candidates.ToList();
            var modules=packages.SelectMany(p=>p.Manifest.Modules.Select(m=>Tuple.Create(p,m))).ToList();
            if(modules.Count+host.ModuleDeclarations.Count>4096) throw ManagedExtensionJson.Error("ManagedModuleGraphLimit");
            var unavailable=new HashSet<string>(disabled,StringComparer.OrdinalIgnoreCase);
            var map=modules.ToDictionary(pair=>pair.Item2.Id,pair=>pair.Item2.DependsOn,StringComparer.OrdinalIgnoreCase);
            foreach(var declared in host.ModuleDeclarations)
            {
                if(map.ContainsKey(declared.Id)) throw ManagedExtensionJson.Error("ManagedHostModuleConflict");
                map.Add(declared.Id,declared.DependsOn);
            }
            var codes=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string id in map.Keys)
            {
                token.ThrowIfCancellationRequested();
                var pending=new Stack<string>(map[id]); var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while(pending.Count!=0)
                {
                    string next=pending.Pop();
                    if(string.Equals(next,id,StringComparison.OrdinalIgnoreCase))
                    {
                        unavailable.Add(id);
                        var owner=modules.FirstOrDefault(pair=>string.Equals(pair.Item2.Id,id,StringComparison.OrdinalIgnoreCase));
                        if(owner!=null) codes[owner.Item1.RecordKey]="ManagedModuleDependencyCycle";
                        break;
                    }
                    if(!visited.Add(next)) continue;
                    System.Collections.ObjectModel.ReadOnlyCollection<string> deps;
                    if(map.TryGetValue(next,out deps)) foreach(string dep in deps) pending.Push(dep);
                }
            }
            bool changed;
            do
            {
                token.ThrowIfCancellationRequested(); changed=false;
                foreach(var pair in map)
                    if(!unavailable.Contains(pair.Key) && pair.Value.Any(id=>unavailable.Contains(id) || !map.ContainsKey(id) && !host.AvailableModuleIds.Contains(id,StringComparer.OrdinalIgnoreCase)))
                    { unavailable.Add(pair.Key); changed=true; }
            } while(changed);
            foreach(var p in packages)
            {
                if(codes.ContainsKey(p.RecordKey)) continue;
                if(p.Manifest.Modules.All(m=>disabled.Contains(m.Id))) codes[p.RecordKey]="ManagedAllModulesDisabled";
                else if(p.Manifest.Modules.Any(m=>!disabled.Contains(m.Id) && unavailable.Contains(m.Id))) codes[p.RecordKey]="ManagedModuleDependencyUnavailable";
            }
            return codes;
        }
    }
}
