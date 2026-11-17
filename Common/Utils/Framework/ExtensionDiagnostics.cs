using System;
using System.Collections.Generic;
using System.Threading;

namespace Utils.Framework
{
    /// <summary>Best-effort diagnostics at ownership boundaries. Never changes operation outcomes.</summary>
    public static class ExtensionDiagnostics
    {
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static bool emergencyWrite;
        private static long suppressedWrites;
        public static long SuppressedWrites => Interlocked.Read(ref suppressedWrites);
        public static void Report(ExtensionHostContext context, string owner, string operation, Exception error)
        {
            Write(context?.StructuredLog, context?.Log, new HostLogEntry(owner, LogLevel.ERROR, operation, error));
        }

        public static void Write(Action<HostLogEntry> structured, Action<string, LogLevel> legacy, HostLogEntry entry)
        {
            if (entry == null) return;
            // Scoped Host log bridges legitimately nest. A cyclic optional observer must still be bounded.
            if (depth >= 8)
            {
                Interlocked.Increment(ref suppressedWrites);
                Emergency(entry);
                return;
            }
            depth++;
            try
            {
                var healthy = new List<Action<HostLogEntry>>();
                List<Exception> failures = null;
                if (structured != null)
                    foreach (Action<HostLogEntry> sink in structured.GetInvocationList())
                        try { sink(entry); healthy.Add(sink); }
                        catch (Exception error) { (failures ?? (failures = new List<Exception>())).Add(error); }
                if (healthy.Count == 0 && legacy != null)
                    foreach (Action<string, LogLevel> sink in legacy.GetInvocationList())
                        try { sink(Format(entry), entry.Level); }
                        catch (Exception error) { (failures ?? (failures = new List<Exception>())).Add(error); }
                if (healthy.Count == 0 && legacy == null) Emergency(entry);

                if (failures == null) return;
                var secondary = new HostLogEntry(entry.ExtensionId, LogLevel.ERROR, "DiagnosticObserverFailed",
                    failures.Count == 1 ? failures[0] : new AggregateException("Diagnostic observers failed.", failures), entry.CorrelationId);
                bool delivered = false;
                // Only observers that accepted the original get the secondary fault. Never recurse into a bad sink.
                foreach (var sink in healthy)
                    try { sink(secondary); delivered = true; } catch { Interlocked.Increment(ref suppressedWrites); }
                if (!delivered && legacy != null)
                    foreach (Action<string, LogLevel> sink in legacy.GetInvocationList())
                        try { sink(Format(secondary), secondary.Level); delivered = true; } catch { Interlocked.Increment(ref suppressedWrites); }
                if (!delivered) Emergency(secondary);
            }
            finally { depth--; }
        }

        private static string Format(HostLogEntry entry)
        {
            try { return entry.ToDisplayMessage(); }
            catch
            {
                try { return entry.Message + "\n" + entry.Exception?.GetType().FullName + "\n" + new System.Diagnostics.StackTrace(entry.Exception, false); }
                catch { return "Diagnostic formatting failed; original exception propagation is unchanged."; }
            }
        }

        public static void TraceSensitive(string operation,Exception error)
        {
            if(emergencyWrite){Interlocked.Increment(ref suppressedWrites);return;}
            emergencyWrite=true;
            try {
                var text=new System.Text.StringBuilder(operation+"; messages omitted at sensitive boundary\n");
                var queue=new Queue<Exception>();queue.Enqueue(error);int remaining=64;
                while(queue.Count>0 && remaining-->0 && text.Length<65500){
                    var current=queue.Dequeue();text.AppendLine(current.GetType().FullName);text.AppendLine(new System.Diagnostics.StackTrace(current,false).ToString());
                    if(current is AggregateException aggregate){foreach(var cause in aggregate.InnerExceptions)if(queue.Count<64)queue.Enqueue(cause);}
                    else if(current.InnerException!=null)queue.Enqueue(current.InnerException);
                    if(current is System.Reflection.ReflectionTypeLoadException types && types.LoaderExceptions!=null)
                        foreach(var cause in types.LoaderExceptions)if(cause!=null && queue.Count<64)queue.Enqueue(cause);
                }
                string value=text.ToString(0,Math.Min(text.Length,65500));if(queue.Count>0 || text.Length>65500)value+="\n[diagnostic size limit reached]";
                foreach(System.Diagnostics.TraceListener sink in System.Diagnostics.Trace.Listeners)
                    try{sink.TraceEvent(new System.Diagnostics.TraceEventCache(),"Phinix",System.Diagnostics.TraceEventType.Warning,0,value);}catch{Interlocked.Increment(ref suppressedWrites);}
            }catch{Interlocked.Increment(ref suppressedWrites);}finally{emergencyWrite=false;}
        }

        private static void Emergency(HostLogEntry entry)
        {
            if (emergencyWrite) return;
            emergencyWrite = true;
            try { System.Diagnostics.Trace.TraceError(Format(entry)); }
            catch { Interlocked.Increment(ref suppressedWrites); }
            finally { emergencyWrite = false; }
        }
    }
}
