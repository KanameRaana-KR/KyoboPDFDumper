using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace KyoboDumper
{
    public sealed class HookAppDomainManager : AppDomainManager
    {
        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            try { Bootstrap.Initialize(); }
            catch (Exception ex)
            {
                try { File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "kyobo_dumper_boot.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + "InitializeNewDomain failed: " + ex + Environment.NewLine); }
                catch { }
            }
            base.InitializeNewDomain(appDomainInfo);
        }
    }

    internal static class Bootstrap
    {
        internal static string DumpRoot;
        internal static Harmony HarmonyInstance;
        private static string HookDir;
        private static bool _initialized;
        private static readonly object _lock = new object();
        private static readonly HashSet<string> _hookedAsms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;
            }

            HookDir = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location);

            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    var name = new AssemblyName(e.Name).Name + ".dll";
                    var cand = Path.Combine(HookDir, name);
                    if (File.Exists(cand)) return Assembly.LoadFrom(cand);
                }
                catch { }
                return null;
            };

            DumpRoot = Environment.GetEnvironmentVariable("KYOBO_DUMP_PATH");
            if (string.IsNullOrWhiteSpace(DumpRoot))
                DumpRoot = Path.Combine(Path.GetTempPath(), "kyobo_dump");
            Directory.CreateDirectory(DumpRoot);

            Log("[KyoboDumper] AppDomainManager alive. Dump dir: " + DumpRoot);
            Log("[KyoboDumper] PID=" + Pid);

            HarmonyInstance = new Harmony("nyx.kyobo.dumper");

            AppDomain.CurrentDomain.AssemblyLoad += (s, e) => TryHook(e.LoadedAssembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                TryHook(asm);
        }

        private static void TryHook(Assembly asm)
        {
            if (asm == null) return;
            string name = asm.GetName().Name ?? "";

            lock (_lock)
            {
                if (_hookedAsms.Contains(name)) return;
            }

            try
            {
                if (name.Equals("KyoboBook.Ebook.Controls", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_lock) { _hookedAsms.Add(name); }
                    PdfPatches.HookControls(HarmonyInstance, asm);
                }
                else if (name.Equals("KyoboBook.Ebook.Platform", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_lock) { _hookedAsms.Add(name); }
                    PdfPatches.HookPlatform(HarmonyInstance, asm);
                }
                else if (name.Equals("KyoboBook.Ebook.Platform.Container", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_lock) { _hookedAsms.Add(name); }
                    PdfPatches.HookContainer(HarmonyInstance, asm);
                }
            }
            catch (Exception ex)
            {
                Log("[KyoboDumper] Hook error on " + name + ": " + ex);
            }
        }

        internal static int Pid { get { return System.Diagnostics.Process.GetCurrentProcess().Id; } }

        internal static void Log(string msg)
        {
            try
            {
                Console.WriteLine(msg);
                string dir = DumpRoot ?? Path.GetTempPath();
                string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [pid " + Pid + "] " + msg + Environment.NewLine;
                File.AppendAllText(Path.Combine(dir, "dumper.log"), line);
            }
            catch { }
        }
    }
}
