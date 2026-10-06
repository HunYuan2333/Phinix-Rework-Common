using System;
using System.IO;

namespace Utils.Framework.ManagedExtensions
{
    /// <summary>Shared serialization boundary for host startup and future owned-state mutations.</summary>
    public sealed class ManagedExtensionLease : IDisposable
    {
        private FileStream stream;
        private ManagedExtensionLease(FileStream stream) { this.stream = stream; }
        public static ManagedExtensionLease Acquire(ManagedExtensionPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            string state = Path.Combine(paths.RootDirectory, "state"), file = Path.Combine(state, "runtime.lock");
            ManagedExtensionInventoryReader.NoLinks(file);
            Directory.CreateDirectory(state); ManagedExtensionInventoryReader.NoLinks(file);
            FileStream stream = null;
            try
            {
                stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // FileShare alone is not sufficient cross-process on all Mono/Unix versions.
#if NET10_0_OR_GREATER
                if (OperatingSystem.IsMacOS()) throw new ManagedExtensionValidationException("ManagedLockPlatformUnsupported");
#endif
                stream.Lock(0, 1);
                return new ManagedExtensionLease(stream);
            }
            catch (IOException ex) { stream?.Dispose(); throw new ManagedExtensionValidationException("ManagedStoreBusy", ex); }
            catch { stream?.Dispose(); throw; }
        }
        public void Dispose() { var held = stream; stream = null; held?.Dispose(); }
    }
}
