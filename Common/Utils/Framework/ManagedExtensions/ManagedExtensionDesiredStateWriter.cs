using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    internal static class ManagedExtensionDesiredStateWriter
    {
        // Caller holds the same lifetime runtime lease. No delete/move replacement fallback.
        internal static string Write(ManagedExtensionPaths paths, ManagedExtensionPackageSnapshot row,
            ManagedExtensionDesiredState desired, CancellationToken token, Action<string> fault = null, Action<string> prepared = null)
        {
            string value;
            switch(desired)
            {
                case ManagedExtensionDesiredState.Enabled: value="enabled"; break;
                case ManagedExtensionDesiredState.Disabled: value="disabled"; break;
                case ManagedExtensionDesiredState.PendingRemoval: value="pending-removal"; break;
                default: throw ManagedExtensionJson.Error("InvalidDesiredState");
            }
            string operation=Guid.NewGuid().ToString("N");
            byte[] bytes=Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"sourceId\":\""+row.SourceId+"\",\"packageId\":\""+row.PackageId+
                "\",\"manifestSha256\":\""+row.ManifestSha256+"\",\"operationId\":\""+operation+"\",\"desiredState\":\""+value+"\"}");
            string destination=paths.GetDesiredStatePath(row.SourceId,row.PackageId);
            ManagedExtensionInventoryReader.NoLinks(destination);
            byte[] previous=ManagedExtensionInventoryReader.Bytes(destination,8192,token);
            // Bind the original raw state as well as the inventory's validated identity.
            var f=ManagedExtensionJson.Object(ManagedExtensionJson.Read(previous,8192),"schemaVersion","sourceId","packageId","manifestSha256","operationId","desiredState");
            string previousValue=row.DesiredState==ManagedExtensionDesiredState.Enabled?"enabled":row.DesiredState==ManagedExtensionDesiredState.Disabled?"disabled":"pending-removal";
            if(ManagedExtensionJson.Integer(ManagedExtensionJson.Required(f,"schemaVersion"),1,1)!=1 ||
                ManagedExtensionJson.Id(ManagedExtensionJson.Required(f,"sourceId"))!=row.SourceId ||
                ManagedExtensionJson.Id(ManagedExtensionJson.Required(f,"packageId"))!=row.PackageId ||
                ManagedExtensionJson.Hex(ManagedExtensionJson.Required(f,"manifestSha256"),64)!=row.ManifestSha256 ||
                ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,"desiredState"),32)!=previousValue ||
                ManagedExtensionJson.Hex(ManagedExtensionJson.Required(f,"operationId"),32)!=row.StateOperationId)
                throw ManagedExtensionJson.Error("ManagedStateChanged");
            string receiptPath=paths.GetInstalledRecordPath(row.SourceId,row.PackageId);
            ManagedExtensionInventoryReader.NoLinks(receiptPath);
            byte[] receiptBytes=ManagedExtensionInventoryReader.Bytes(receiptPath,ManagedExtensionInventoryReader.MaxRecordBytes,token);
            var receipt=ManagedExtensionInventoryReader.ReadReceipt(receiptBytes);
            if(receipt.SourceId!=row.SourceId || receipt.PackageId!=row.PackageId || receipt.ManifestHash!=row.ManifestSha256 || receipt.TransactionId!=row.InstallationTransactionId ||
                receipt.Version!=row.Version || receipt.IdentityHash!=row.RepositoryIdentitySha256 || receipt.SnapshotId!=row.CatalogSnapshotId || receipt.CatalogHash!=row.CatalogSha256 || receipt.ArtifactHash!=row.ArtifactSha256)
                throw ManagedExtensionJson.Error("ManagedOwnershipChanged");
            string temporary=Path.Combine(paths.DesiredStateDirectory,".state-"+operation+".tmp");
            bool created=false, replacing=false;
            try
            {
                token.ThrowIfCancellationRequested(); ManagedExtensionInventoryReader.NoLinks(temporary);
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { created=true; output.Write(bytes,0,bytes.Length); output.Flush(true); }
                fault?.Invoke("state-flushed"); token.ThrowIfCancellationRequested();
                ManagedExtensionInventoryReader.NoLinks(destination); ManagedExtensionInventoryReader.NoLinks(temporary);
                if(!ManagedExtensionInventoryReader.Bytes(destination,8192,token).SequenceEqual(previous)) throw ManagedExtensionJson.Error("ManagedStateChanged");
                if(!ManagedExtensionInventoryReader.Bytes(temporary,8192,token).SequenceEqual(bytes)) throw ManagedExtensionJson.Error("ManagedStateTemporaryChanged");
                ManagedExtensionInventoryReader.NoLinks(receiptPath);
                if(!ManagedExtensionInventoryReader.Bytes(receiptPath,ManagedExtensionInventoryReader.MaxRecordBytes,token).SequenceEqual(receiptBytes)) throw ManagedExtensionJson.Error("ManagedOwnershipChanged");
                ManagedExtensionInventoryReader.VerifyTree(paths.GetPackageDirectory(row.SourceId,row.PackageId),receipt.Files,token);
                prepared?.Invoke(operation); replacing=true;
                File.Replace(temporary,destination,null); // Commit point; cancellation after this cannot undo intent.
                fault?.Invoke("state-committed"); return operation;
            }
            catch(IOException ex) { if(replacing) throw new ManagedExtensionValidationException("ManagedStateWriteUncertain",ex); throw; }
            catch(UnauthorizedAccessException ex) { if(replacing) throw new ManagedExtensionValidationException("ManagedStateWriteUncertain",ex); throw; }
            finally
            {
                // A changed file is evidence; cleanup only this operation's exact owned temporary bytes.
                if(created) try
                {
                    ManagedExtensionInventoryReader.NoLinks(temporary);
                    if(File.Exists(temporary) && ManagedExtensionInventoryReader.Bytes(temporary,8192,CancellationToken.None).SequenceEqual(bytes)) File.Delete(temporary);
                }
                catch(IOException) { } catch(UnauthorizedAccessException) { } catch(ManagedExtensionValidationException) { }
            }
        }
    }
}
