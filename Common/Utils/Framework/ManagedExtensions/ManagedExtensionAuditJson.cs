using System.Text;

namespace Utils.Framework.ManagedExtensions
{
    public static class ManagedExtensionAuditJson
    {
        public static string Format(ManagedExtensionRuntimeAudit entry)
        {
            if(entry==null) throw new System.ArgumentNullException(nameof(entry));
            var row=entry.Package;
            return "{\"schemaVersion\":1,\"event\":\"managed_extension\",\"time\":"+Q(entry.TimeUtc.ToString("o",System.Globalization.CultureInfo.InvariantCulture))+",\"sequence\":"+entry.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"startupId\":"+Q(entry.StartupId)+
                ",\"stage\":"+Q(entry.Stage)+",\"code\":"+Q(entry.Code)+",\"sourceId\":"+Q(row?.SourceId)+",\"packageId\":"+Q(row?.PackageId)+
                ",\"recordKey\":"+Q(row?.RecordKey)+",\"version\":"+Q(row?.Version)+",\"manifestSha256\":"+Q(row?.ManifestSha256)+",\"catalogSnapshotId\":"+Q(row?.CatalogSnapshotId)+
                ",\"catalogSha256\":"+Q(row?.CatalogSha256)+",\"artifactSha256\":"+Q(row?.ArtifactSha256)+",\"installationTransactionId\":"+Q(row?.InstallationTransactionId)+
                ",\"stateOperationId\":"+Q(row?.StateOperationId)+",\"assemblyName\":"+Q(entry.AssemblyName)+",\"moduleId\":"+Q(entry.ModuleId)+
                ",\"resourcePath\":"+Q(entry.ResourcePath)+",\"referencingAssembly\":"+Q(entry.ReferenceFailure?.ReferencingAssembly)+",\"requiredReference\":"+Q(entry.ReferenceFailure?.RequiredReference)+
                ",\"availableReferences\":"+(entry.ReferenceFailure==null?"null":"["+string.Join(",",System.Linq.Enumerable.Select(entry.ReferenceFailure.AvailableReferences,Q))+"]")+"}";
        }
        private static string Q(string value)
        {
            if(value==null) return "null";
            var result=new StringBuilder("\"");
            foreach(char c in value)
                if(c=='"' || c=='\\') result.Append('\\').Append(c);
                else if(c<32 || char.IsSurrogate(c)) result.Append("\\u").Append(((int)c).ToString("x4",System.Globalization.CultureInfo.InvariantCulture));
                else result.Append(c);
            return result.Append('"').ToString();
        }
    }
}
