using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Utils.Framework
{
    /// <summary>Explicit bundle roots: loose DLLs plus immediate plugin folders, never recursive resource probing.</summary>
    public static class ExtensionBundleDirectories
    {
        public static IEnumerable<string> GetProbeDirectories(string root,Action<string,LogLevel> log=null)
        {
            if(string.IsNullOrWhiteSpace(root)) yield break;
            root=Path.GetFullPath(root);
            yield return root;
            if(!Directory.Exists(root)) yield break;
            string[] directories=null;
            try { directories=Directory.EnumerateDirectories(root,"*",SearchOption.TopDirectoryOnly).OrderBy(p=>p,StringComparer.Ordinal).ToArray(); }
            catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
            { log?.Invoke("ExtensionBundleEnumerationFailed: "+error.GetType().Name,LogLevel.WARNING); }
            if(directories==null) yield break;
            foreach(string directory in directories)
            {
                if(string.Equals(Path.GetFileName(directory),"Resources",StringComparison.OrdinalIgnoreCase)) continue;
                bool hasDll=false;
                try { hasDll=Directory.EnumerateFiles(directory,"*.dll",SearchOption.TopDirectoryOnly).Any(); }
                catch(Exception error) when(error is IOException || error is UnauthorizedAccessException)
                { log?.Invoke("ExtensionBundleReadFailed: "+Path.GetFileName(directory)+"; "+error.GetType().Name,LogLevel.WARNING); }
                if(hasDll) yield return directory;
            }
        }
    }
}
