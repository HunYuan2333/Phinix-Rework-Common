namespace Utils.Framework.ManagedExtensions
{
    // Installation owns replacement/lifecycle state. Offline inspectors only need the ZIP reader.
    public sealed partial class ManagedExtensionZip
    {
        public ManagedExtensionInstallPackage LocalInstallation(ManagedExtensionPackageSnapshot replacement=null)
            => ManagedExtensionInstallPackage.LocalDevelopment(Sha256,manifestBytes,content,replacement);
        public ManagedExtensionInstallPackage RepositoryInstallation(string source,string identity,string snapshot,string catalog,ManagedExtensionPackageSnapshot replacement=null)
            => new ManagedExtensionInstallPackage(source,identity,snapshot,catalog,Sha256,manifestBytes,content,replacement);
    }
}
