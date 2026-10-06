using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    // Filesystem adapters stay out of the static publication validator snapshot.
    public sealed partial class ExtensionLocalizationCatalog
    {
        public static ExtensionLocalizationCatalog LoadDirectory(string root,ExtensionLocalizationDeclaration declaration,CancellationToken token)
        {
            return Load(declaration,file=>
            {
                string path=Path.Combine(root,file.Path); ManagedExtensionInventoryReader.NoLinks(path);
                return ManagedExtensionInventoryReader.Bytes(path,ExtensionLocalizationDeclaration.MaxFileBytes,token);
            },token);
        }
        /// <summary>Explicit companion for normally discovered bundled DLLs, not native Mod languages.</summary>
        public static ExtensionLocalizationCatalog LoadCompanion(string assemblyPath,string assemblyName,CancellationToken token)
        {
            string path=assemblyPath+".localization.json"; ManagedExtensionInventoryReader.NoLinks(path);
            if(!File.Exists(path)) return Empty;
            var fields=ManagedExtensionJson.Object(ManagedExtensionJson.Read(ManagedExtensionInventoryReader.Bytes(path,128*1024,token),128*1024),"schemaVersion","assemblyName","resources","localization");
            if(ManagedExtensionJson.Integer(ManagedExtensionJson.Required(fields,"schemaVersion"),1,int.MaxValue)!=1 || ManagedExtensionJson.Text(ManagedExtensionJson.Required(fields,"assemblyName"),128)!=assemblyName)
                throw ManagedExtensionJson.Error("LocalizationOwnerMismatch");
            var resources=ManagedExtensionJson.Array(ManagedExtensionJson.Required(fields,"resources"),16).Select(n=>ManagedExtensionManifestReader.File(ManagedExtensionJson.Object(n,"path","length","sha256"))).ToList();
            if(resources.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=resources.Count || resources.Any(f=>!f.Path.StartsWith("Resources/",StringComparison.Ordinal))) throw ManagedExtensionJson.Error("InvalidLocalizationResource");
            ManagedExtensionManifestReader.ValidatePathTree(resources.Select(f=>f.Path));
            return LoadDirectory(Path.GetDirectoryName(assemblyPath),ExtensionLocalizationDeclaration.Read(ManagedExtensionJson.Required(fields,"localization"),resources),token);
        }
    }
}
