using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Utils.Framework.ManagedExtensions
{
    // Shared diagnostic contract, independent of runtime planning and installed inventory.
    public sealed class ManagedExtensionAssemblyReferenceFailure
    {
        internal ManagedExtensionAssemblyReferenceFailure(string assembly,ManagedAssemblyIdentity required,IEnumerable<string> available)
        { ReferencingAssembly=assembly; RequiredReference=required.FullName; AvailableReferences=ManagedExtensionCompatibility.Freeze(available.Distinct(StringComparer.Ordinal).OrderBy(v=>v,StringComparer.Ordinal)); }
        public string ReferencingAssembly { get; }
        public string RequiredReference { get; }
        public ReadOnlyCollection<string> AvailableReferences { get; }
    }
}
