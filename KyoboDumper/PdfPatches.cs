using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace KyoboDumper
{
    /// <summary>
    /// Kyobo eLibrary Universal Dumper:
    /// - PDF: Clean decrypted PDF dump
    /// - EPUB: Full compliant .epub archive with 100% decrypted Fasoo chapters & intact metadata
    /// - Comic: Auto AES-128-CBC page decryption & .cbz package creation
    /// </summary>
    internal static class PdfPatches
    {
        private static readonly HashSet<string> _processedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string _currentBookTitle = null;
        private static object _drmServiceInstance = null;

        // ================================================================
        // 1. Controls Hook (UI events & title discovery)
        // ================================================================
        public static void HookControls(Harmony h, Assembly asm)
        {
            int hooked = 0;
            try
            {
                var libPresenter = asm.GetType("KyoboBook.Ebook.ELibrary.Presenters.LibraryPresenter");
                if (libPresenter != null)
                {
                    var openMedia = FindMethod(libPresenter, "OpenMedia");
                    if (openMedia != null && TryPatch(h, openMedia, nameof(Pre_OpenMedia), isPrefix: true))
                    {
                        Bootstrap.Log("[Hook] Hooked LibraryPresenter.OpenMedia");
                        hooked++;
                    }
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[Hook] HookControls error: " + ex.Message);
            }
            Bootstrap.Log(string.Format("[Hook] Controls assembly: {0} method(s) patched.", hooked));
        }

        // ================================================================
        // 2. Platform Hook (Zip extraction, DirectoryDelete, DrmService)
        // ================================================================
        public static void HookPlatform(Harmony h, Assembly asm)
        {
            int hooked = 0;

            // Hook Zip.Extract overloads
            try
            {
                var zipType = asm.GetType("KyoboBook.Ebook.Platform.Helper.Zip");
                if (zipType != null)
                {
                    foreach (var m in FindMethods(zipType, "Extract"))
                    {
                        if (TryPatch(h, m, nameof(Post_ZipExtract), isPrefix: false))
                        {
                            Bootstrap.Log("[Hook] Hooked Helper.Zip.Extract (" + m.GetParameters().Length + " params)");
                            hooked++;
                        }
                    }
                }
            }
            catch (Exception ex) { Bootstrap.Log("[Hook] Zip hook error: " + ex.Message); }

            // Hook IOHelper.DirectoryDelete
            try
            {
                var ioHelper = asm.GetType("KyoboBook.Ebook.Platform.Helper.IOHelper");
                if (ioHelper != null)
                {
                    foreach (var m in FindMethods(ioHelper, "DirectoryDelete"))
                    {
                        if (TryPatch(h, m, nameof(Pre_DirectoryDelete), isPrefix: true))
                        {
                            Bootstrap.Log("[Hook] Hooked Helper.IOHelper.DirectoryDelete");
                            hooked++;
                        }
                    }
                }
            }
            catch (Exception ex) { Bootstrap.Log("[Hook] IOHelper hook error: " + ex.Message); }

            // Hook DrmService methods
            try
            {
                var drmServiceType = asm.GetType("KyoboBook.Ebook.Platform.Drm.DrmService");
                if (drmServiceType != null)
                {
                    foreach (var m in FindMethods(drmServiceType, "OpenDrmContent"))
                    {
                        if (TryPatch(h, m, nameof(Post_GenericDrmBytes), isPrefix: false))
                        {
                            Bootstrap.Log("[Hook] Hooked DrmService.OpenDrmContent");
                            hooked++;
                        }
                    }

                    foreach (var m in FindMethods(drmServiceType, "OpenDrmContentFilePath"))
                    {
                        if (TryPatch(h, m, nameof(Post_GenericDrmFilePath), isPrefix: false))
                        {
                            Bootstrap.Log("[Hook] Hooked DrmService.OpenDrmContentFilePath");
                            hooked++;
                        }
                    }

                    foreach (var m in FindMethods(drmServiceType, "OpenSecondDrmContent"))
                    {
                        if (TryPatch(h, m, nameof(Post_OpenSecondDrmContent), isPrefix: false))
                        {
                            Bootstrap.Log("[Hook] Hooked DrmService.OpenSecondDrmContent");
                            hooked++;
                        }
                    }
                }
            }
            catch (Exception ex) { Bootstrap.Log("[Hook] DrmService hook error: " + ex.Message); }

            Bootstrap.Log(string.Format("[Hook] Platform assembly: {0} method(s) patched.", hooked));
        }

        // ================================================================
        // 3. Container Hook (MediaSource & Decrypt)
        // ================================================================
        public static void HookContainer(Harmony h, Assembly asm)
        {
            int hooked = 0;
            try
            {
                var mediaSourceType = asm.GetType("KyoboBook.Ebook.Platform.Container.MediaSource");
                if (mediaSourceType != null)
                {
                    // Hook MediaSource.Extract() -> byte[]
                    foreach (var m in FindMethods(mediaSourceType, "Extract"))
                    {
                        if (m.ReturnType == typeof(byte[]) && TryPatch(h, m, nameof(Post_MediaSourceExtract), isPrefix: false))
                        {
                            Bootstrap.Log("[Hook] Hooked MediaSource.Extract");
                            hooked++;
                        }
                    }

                    // Hook MediaSource.ExtractFilePath() -> string
                    foreach (var m in FindMethods(mediaSourceType, "ExtractFilePath"))
                    {
                        if (m.ReturnType == typeof(string) && TryPatch(h, m, nameof(Post_GenericDrmFilePath), isPrefix: false))
                        {
                            Bootstrap.Log("[Hook] Hooked MediaSource.ExtractFilePath");
                            hooked++;
                        }
                    }

                    // Hook MediaSource.ExtractCore(...) -> object
                    foreach (var m in FindMethods(mediaSourceType, "ExtractCore"))
                    {
                        if (TryPatch(h, m, nameof(Post_MediaSourceExtractCore), isPrefix: false))
                        {
                            Bootstrap.Log("[Hook] Hooked MediaSource.ExtractCore");
                            hooked++;
                        }
                    }
                }

                // Scan all types for ExtractFilePath and Decrypt methods
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray(); }

                foreach (var t in types)
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.Static |
                                               BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }

                    foreach (var m in methods)
                    {
                        if (m.Name == "ExtractFilePath" && m.ReturnType == typeof(string) && m.DeclaringType != mediaSourceType)
                        {
                            if (TryPatch(h, m, nameof(Post_GenericDrmFilePath), isPrefix: false))
                            {
                                Bootstrap.Log(string.Format("[Hook] Hooked {0}.ExtractFilePath", t.Name));
                                hooked++;
                            }
                        }
                        else if (m.Name == "Decrypt" && m.ReturnType == typeof(byte[]))
                        {
                            if (TryPatch(h, m, nameof(Post_FallbackDecrypt), isPrefix: false))
                            {
                                hooked++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[Hook] HookContainer error: " + ex.Message);
            }
            Bootstrap.Log(string.Format("[Hook] Container assembly: {0} method(s) patched.", hooked));
        }

        // ================================================================
        // Helper Method Finders
        // ================================================================

        private static MethodInfo FindMethod(Type type, string name)
        {
            if (type == null) return null;
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == name);
            }
            catch { return null; }
        }

        private static IEnumerable<MethodInfo> FindMethods(Type type, string name)
        {
            if (type == null) return Enumerable.Empty<MethodInfo>();
            try
            {
                return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == name);
            }
            catch { return Enumerable.Empty<MethodInfo>(); }
        }

        private static bool TryPatch(Harmony h, MethodInfo target, string patchMethodName, bool isPrefix)
        {
            if (target == null) return false;
            try
            {
                var method = typeof(PdfPatches).GetMethod(patchMethodName, BindingFlags.Static | BindingFlags.NonPublic);
                var hm = new HarmonyMethod(method);

                if (isPrefix)
                    h.Patch(target, prefix: hm);
                else
                    h.Patch(target, postfix: hm);

                return true;
            }
            catch (Exception ex)
            {
                Bootstrap.Log(string.Format("[Hook]  patch FAIL {0}.{1}: {2}", target.DeclaringType?.Name, target.Name, ex.Message));
                return false;
            }
        }

        // ================================================================
        // Handlers
        // ================================================================

        private static void Pre_OpenMedia(object __instance, object[] __args)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] != null)
                {
                    var media = __args[0];
                    string title = GetMediaTitle(media);
                    string filePath = GetMediaFilePath(media);
                    string fileType = GetMediaFileType(media);

                    _currentBookTitle = title;
                    Bootstrap.Log(string.Format("[BookOpen] >>> USER OPENED BOOK: \"{0}\" | Type={1} | Path={2}", title, fileType, filePath));
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[BookOpen] Error in Pre_OpenMedia: " + ex);
            }
        }

        private static void Post_ZipExtract(object[] __args, object __result)
        {
            try
            {
                if (__args == null) return;

                // Check targetDirectory argument
                for (int i = 0; i < __args.Length; i++)
                {
                    if (__args[i] is string dir && Directory.Exists(dir))
                    {
                        Bootstrap.Log("[ZipExtract] Extracted to: " + dir);
                        ProcessExtractedDirectory(dir);
                    }
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[ZipExtract] Error: " + ex.Message);
            }
        }

        private static void Pre_DirectoryDelete(object[] __args)
        {
            try
            {
                if (__args != null && __args.Length >= 1 && __args[0] is string dirPath)
                {
                    if (dirPath.IndexOf("ELibrary", StringComparison.OrdinalIgnoreCase) >= 0 && Directory.Exists(dirPath))
                    {
                        Bootstrap.Log("[DirectoryDelete] Intercepted cleanup for: " + dirPath);
                        ProcessExtractedDirectory(dirPath);
                    }
                }
            }
            catch { }
        }

        private static void Post_OpenSecondDrmContent(object __instance, object[] __args, object __result)
        {
            try
            {
                if (__instance != null) _drmServiceInstance = __instance;

                if (__args != null && __args.Length > 0 && __args[0] is string path && __result is byte[] dec && dec.Length > 0)
                {
                    if (File.Exists(path))
                    {
                        File.WriteAllBytes(path, dec);
                        Bootstrap.Log(string.Format("[EPUB] Replaced Fasoo file on disk with decrypted content: {0} ({1} bytes)",
                            Path.GetFileName(path), dec.Length));

                        // Re-package EPUB with this decrypted chapter!
                        string dir = Path.GetDirectoryName(path);
                        while (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            if (File.Exists(Path.Combine(dir, "mimetype")) || Directory.Exists(Path.Combine(dir, "META-INF")))
                            {
                                string title = _currentBookTitle ?? "book";
                                string safeTitle = SafeTag(title);
                                string outEpub = Path.Combine(Bootstrap.DumpRoot, safeTitle + ".epub");
                                PackageEpub(dir, outEpub);
                                break;
                            }
                            dir = Path.GetDirectoryName(dir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[DrmService] Post_OpenSecondDrmContent error: " + ex.Message);
            }
        }

        private static void Post_GenericDrmFilePath(object[] __args, object __result)
        {
            try
            {
                string path = __result as string;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    HandleDecryptedFile(path, "GenericDrmFilePath");
                }
            }
            catch { }
        }

        private static void Post_MediaSourceExtractCore(object __instance, object[] __args, object __result)
        {
            try
            {
                if (__result is string path && File.Exists(path))
                {
                    HandleDecryptedFile(path, "MediaSource.ExtractCore(path)");
                }
                else if (__result is byte[] data && data.Length > 0)
                {
                    HandleDecryptedBytes(data, "MediaSource.ExtractCore(bytes)");
                }
            }
            catch { }
        }

        private static void Post_GenericDrmBytes(object[] __args, object __result)
        {
            try
            {
                byte[] data = __result as byte[];
                if (data == null || data.Length == 0) return;
                HandleDecryptedBytes(data, "DrmService");
            }
            catch { }
        }

        private static void Post_MediaSourceExtract(object __instance, object __result)
        {
            try
            {
                byte[] data = __result as byte[];
                if (data == null || data.Length == 0) return;
                HandleDecryptedBytes(data, "MediaSource.Extract");
            }
            catch { }
        }

        private static void Post_FallbackDecrypt(object __instance, object[] __args, object __result)
        {
            try
            {
                if (__args != null && __args.Length > 0 && __args[0] is string path && __result is byte[] dec && dec.Length > 0)
                {
                    if (File.Exists(path))
                    {
                        File.WriteAllBytes(path, dec);
                    }
                }
            }
            catch { }
        }

        // ================================================================
        // Processing Core: Proactive Fasoo Chapter Decryption & EPUB Packaging
        // ================================================================

        public static void ProcessExtractedDirectory(string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return;

            lock (_processedDirs)
            {
                if (_processedDirs.Contains(dirPath)) return;
                _processedDirs.Add(dirPath);
            }

            string title = _currentBookTitle ?? "book";
            string safeTitle = SafeTag(title);

            // Case A: EPUB book
            bool isEpub = File.Exists(Path.Combine(dirPath, "mimetype")) ||
                          Directory.Exists(Path.Combine(dirPath, "META-INF")) ||
                          Directory.EnumerateFiles(dirPath, "*.opf", SearchOption.AllDirectories).Any();

            if (isEpub)
            {
                try
                {
                    // 1. Proactively decrypt ALL Fasoo DRM chapters inside the extracted directory!
                    DecryptAllFasooChapters(dirPath);

                    // 2. Package the clean, fully decrypted EPUB!
                    string outEpub = Path.Combine(Bootstrap.DumpRoot, safeTitle + ".epub");
                    PackageEpub(dirPath, outEpub);
                    var fi = new FileInfo(outEpub);
                    Bootstrap.Log(string.Format("[Dump] [SUCCESS] Packaged clean EPUB: {0} ({1} bytes)", outEpub, fi.Length));

                    // 3. Backup EPUB structure
                    string backupDir = Path.Combine(Bootstrap.DumpRoot, safeTitle + "_epub");
                    CopyDirectory(dirPath, backupDir);
                    Bootstrap.Log("[Dump] [SUCCESS] Backed up EPUB structure to: " + backupDir);
                    return;
                }
                catch (Exception ex)
                {
                    Bootstrap.Log("[Dump] EPUB packaging error: " + ex.Message);
                }
            }

            // Case B: Comic book (KyoboDRM key file + encrypted image bins)
            string drmKeyFile = null;
            byte[] keyBytes = null;
            byte[] ivBytes = null;

            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    // DRM key file is either 48 bytes raw or a Fasoo-encrypted file that decrypts to 48 bytes
                    byte[] head = File.ReadAllBytes(file);
                    if (head.Length == 48 && HasAscii(head, 0, "KyoboDRMv1.0.0"))
                    {
                        drmKeyFile = file;
                        string k1 = Encoding.UTF8.GetString(head, 16, 16);
                        string k2 = Encoding.UTF8.GetString(head, 32, 16);
                        keyBytes = Encoding.UTF8.GetBytes(k1);
                        ivBytes = Encoding.UTF8.GetBytes(k2);
                        break;
                    }
                    else if ((head[0] == 0x9B && head[2] == 'D' && head[3] == 'R' && head[4] == 'M') || HasAscii(head, 0, "DRMONE"))
                    {
                        byte[] dec = CallOpenSecondDrm(file);
                        if (dec != null && dec.Length == 48 && HasAscii(dec, 0, "KyoboDRMv1.0.0"))
                        {
                            drmKeyFile = file;
                            string k1 = Encoding.UTF8.GetString(dec, 16, 16);
                            string k2 = Encoding.UTF8.GetString(dec, 32, 16);
                            keyBytes = Encoding.UTF8.GetBytes(k1);
                            ivBytes = Encoding.UTF8.GetBytes(k2);
                            break;
                        }
                    }
                }
                catch { }
            }

            if (keyBytes != null && ivBytes != null)
            {
                string stagingDir = Path.Combine(Path.GetTempPath(), "comic_staging_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Bootstrap.Log(string.Format("[Comic] Found KyoboDRM key: Key={0}, IV={1}. Decrypting all pages in one shot...",
                        Encoding.UTF8.GetString(keyBytes), Encoding.UTF8.GetString(ivBytes)));
                    Directory.CreateDirectory(stagingDir);

                    // Prefer "img" directory if present, and strictly exclude any "thumb" directory
                    string imgDir = Directory.GetDirectories(dirPath, "*", SearchOption.TopDirectoryOnly)
                        .FirstOrDefault(d => Path.GetFileName(d).Equals("img", StringComparison.OrdinalIgnoreCase));
                    string scanDir = imgDir ?? dirPath;

                    var encFiles = Directory.EnumerateFiles(scanDir, "*", SearchOption.AllDirectories)
                        .Where(f => !f.Equals(drmKeyFile, StringComparison.OrdinalIgnoreCase))
                        .Where(f => f.IndexOf("\\thumb\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    f.IndexOf("/thumb/", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    !Path.GetFileName(Path.GetDirectoryName(f) ?? "").Equals("thumb", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => ExtractNumber(f))
                        .ToList();

                    int pageIdx = 1;
                    foreach (var f in encFiles)
                    {
                        try
                        {
                            byte[] encData = File.ReadAllBytes(f);
                            byte[] decData = DecryptAes128Cbc(encData, keyBytes, ivBytes);
                            if (decData != null && decData.Length > 4)
                            {
                                bool isJpg = decData[0] == 0xFF && decData[1] == 0xD8;
                                bool isPng = decData[0] == 0x89 && decData[1] == 0x50;
                                if (isJpg || isPng)
                                {
                                    // Skip small thumbnail images (< 35KB)
                                    if (decData.Length < 35000 && encFiles.Count > 10) continue;

                                    string ext = isJpg ? ".jpg" : ".png";
                                    string pageName = string.Format("page_{0:D4}{1}", pageIdx++, ext);
                                    File.WriteAllBytes(Path.Combine(stagingDir, pageName), decData);
                                }
                            }
                        }
                        catch { }
                    }

                    if (pageIdx > 1)
                    {
                        // Package to CBZ
                        string cbzPath = Path.Combine(Bootstrap.DumpRoot, safeTitle + ".cbz");
                        if (File.Exists(cbzPath)) File.Delete(cbzPath);
                        ZipFile.CreateFromDirectory(stagingDir, cbzPath);

                        var cbzInfo = new FileInfo(cbzPath);
                        Bootstrap.Log(string.Format("[Dump] [SUCCESS] Packaged complete Comic in ONE SHOT: {0} ({1} pages, {2} bytes)",
                            cbzPath, pageIdx - 1, cbzInfo.Length));

                        // Copy to dump directory
                        string comicDir = Path.Combine(Bootstrap.DumpRoot, safeTitle + "_comic");
                        CopyDirectory(stagingDir, comicDir);
                    }
                }
                catch (Exception ex)
                {
                    Bootstrap.Log("[Comic] Decryption error: " + ex.Message);
                }
                finally
                {
                    try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true); } catch { }
                }
            }
        }

        private static void DecryptAllFasooChapters(string dirPath)
        {
            try
            {
                int decCount = 0;
                foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length < 16) continue;

                        byte[] head = new byte[16];
                        using (var fs = File.OpenRead(file)) { fs.Read(head, 0, 16); }

                        bool isFasoo = (head[0] == 0x9B && head[2] == 'D' && head[3] == 'R' && head[4] == 'M') ||
                                       HasAscii(head, 0, "DRMONE") || HasAscii(head, 1, "DRMONE");

                        if (isFasoo)
                        {
                            byte[] dec = CallOpenSecondDrm(file);
                            if (dec != null && dec.Length > 0)
                            {
                                File.WriteAllBytes(file, dec);
                                Bootstrap.Log(string.Format("[EPUB] Proactively decrypted Fasoo file: {0} ({1} bytes)",
                                    Path.GetFileName(file), dec.Length));
                                decCount++;
                            }
                        }
                    }
                    catch { }
                }
                if (decCount > 0)
                {
                    Bootstrap.Log(string.Format("[EPUB] Total {0} Fasoo chapter(s) decrypted in-place!", decCount));
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[EPUB] DecryptAllFasooChapters error: " + ex.Message);
            }
        }

        private static object GetDrmService()
        {
            if (_drmServiceInstance != null) return _drmServiceInstance;
            try
            {
                var appPlatformType = Type.GetType("KyoboBook.Ebook.Platform.AppPlatform, KyoboBook.Ebook.Platform");
                if (appPlatformType != null)
                {
                    var pProp = appPlatformType.GetProperty("P", BindingFlags.Public | BindingFlags.Static);
                    var p = pProp?.GetValue(null, null);
                    var drmProp = p?.GetType().GetProperty("DrmService", BindingFlags.Public | BindingFlags.Instance);
                    _drmServiceInstance = drmProp?.GetValue(p, null);
                }
            }
            catch { }
            return _drmServiceInstance;
        }

        private static byte[] CallOpenSecondDrm(string filePath)
        {
            var ds = GetDrmService();
            if (ds == null) return null;
            try
            {
                var m = ds.GetType().GetMethod("OpenSecondDrmContent",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new Type[] { typeof(string) }, null);
                if (m != null)
                {
                    return (byte[])m.Invoke(ds, new object[] { filePath });
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[Drm] OpenSecondDrmContent invocation error: " + ex.Message);
            }
            return null;
        }

        private static void HandleDecryptedFile(string filePath, string source)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

                byte[] head = new byte[16];
                using (var fs = File.OpenRead(filePath))
                {
                    fs.Read(head, 0, Math.Min((int)fs.Length, 16));
                }

                string safeTitle = SafeTag(_currentBookTitle ?? Path.GetFileNameWithoutExtension(filePath));

                // 1. PDF
                if (head.Length >= 4 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46)
                {
                    string outFile = Path.Combine(Bootstrap.DumpRoot, safeTitle + ".pdf");
                    File.Copy(filePath, outFile, true);
                    Bootstrap.Log(string.Format("[Dump] [{0}] Saved clean PDF: {1} ({2} bytes)", source, outFile, new FileInfo(outFile).Length));
                    return;
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[Dump] HandleDecryptedFile error: " + ex.Message);
            }
        }

        private static void HandleDecryptedBytes(byte[] data, string source)
        {
            try
            {
                if (data == null || data.Length == 0) return;

                string safeTitle = SafeTag(_currentBookTitle ?? "book");

                // 1. PDF
                if (data.Length >= 4 && data[0] == 0x25 && data[1] == 0x50 && data[2] == 0x44 && data[3] == 0x46)
                {
                    string outFile = Path.Combine(Bootstrap.DumpRoot, safeTitle + ".pdf");
                    File.WriteAllBytes(outFile, data);
                    Bootstrap.Log(string.Format("[Dump] [{0}] Saved clean PDF: {1} ({2} bytes)", source, outFile, data.Length));
                    return;
                }
            }
            catch (Exception ex)
            {
                Bootstrap.Log("[Dump] HandleDecryptedBytes error: " + ex.Message);
            }
        }

        private static byte[] DecryptAes128Cbc(byte[] enc, byte[] key, byte[] iv)
        {
            try
            {
                using (var rij = new RijndaelManaged())
                {
                    rij.KeySize = 128;
                    rij.BlockSize = 128;
                    rij.Key = key;
                    rij.IV = iv;
                    rij.Mode = CipherMode.CBC;
                    rij.Padding = PaddingMode.PKCS7;

                    using (var dec = rij.CreateDecryptor())
                    using (var ms = new MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, dec, CryptoStreamMode.Write))
                        {
                            cs.Write(enc, 0, enc.Length);
                            cs.FlushFinalBlock();
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static void PackageEpub(string sourceDir, string targetEpubPath)
        {
            if (File.Exists(targetEpubPath)) File.Delete(targetEpubPath);

            using (var fs = new FileStream(targetEpubPath, FileMode.Create))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // 1. mimetype MUST be first and UNCOMPRESSED
                string mimePath = Path.Combine(sourceDir, "mimetype");
                var mimeEntry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
                using (var es = mimeEntry.Open())
                {
                    if (File.Exists(mimePath))
                    {
                        byte[] b = File.ReadAllBytes(mimePath);
                        es.Write(b, 0, b.Length);
                    }
                    else
                    {
                        byte[] b = Encoding.ASCII.GetBytes("application/epub+zip");
                        es.Write(b, 0, b.Length);
                    }
                }

                // 2. All other files compressed
                foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(sourceDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                    if (rel.Equals("mimetype", StringComparison.OrdinalIgnoreCase)) continue;

                    var entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                    using (var es = entry.Open())
                    using (var fis = File.OpenRead(file))
                    {
                        fis.CopyTo(es);
                    }
                }
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (string dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = dir.Substring(sourceDir.Length).TrimStart('\\', '/');
                Directory.CreateDirectory(Path.Combine(destDir, rel));
            }
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(sourceDir.Length).TrimStart('\\', '/');
                File.Copy(file, Path.Combine(destDir, rel), true);
            }
        }

        private static string GetMediaTitle(object media)
        {
            if (media == null) return "book";
            try
            {
                var pContent = media.GetType().GetProperty("Content");
                if (pContent != null)
                {
                    var content = pContent.GetValue(media, null);
                    if (content != null)
                    {
                        var pTitle = content.GetType().GetProperty("Title");
                        if (pTitle != null)
                        {
                            var title = pTitle.GetValue(content, null) as string;
                            if (!string.IsNullOrEmpty(title)) return title;
                        }
                    }
                }
            }
            catch { }
            return "book";
        }

        private static string GetMediaFilePath(object media)
        {
            if (media == null) return "<null>";
            try
            {
                var pFilePath = media.GetType().GetProperty("FilePath");
                if (pFilePath != null)
                {
                    return pFilePath.GetValue(media, null) as string ?? "<null>";
                }
            }
            catch { }
            return "<unknown>";
        }

        private static string GetMediaFileType(object media)
        {
            if (media == null) return "<null>";
            try
            {
                var pFileType = media.GetType().GetProperty("FileType");
                if (pFileType != null)
                {
                    var val = pFileType.GetValue(media, null);
                    return val != null ? val.ToString() : "<null>";
                }
            }
            catch { }
            return "<unknown>";
        }

        private static string ComputeHash(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool HasAscii(byte[] b, int offset, string ascii)
        {
            if (b == null || offset + ascii.Length > b.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
                if (b[offset + i] != (byte)ascii[i]) return false;
            return true;
        }

        private static int ExtractNumber(string s)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(s), @"\d+");
            return m.Success && int.TryParse(m.Value, out int val) ? val : 0;
        }

        private static string SafeTag(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "book";
            var name = Path.GetFileNameWithoutExtension(s);
            if (string.IsNullOrEmpty(name)) name = s;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace(' ', '_');
            if (name.Length > 60) name = name.Substring(0, 60);
            return name;
        }
    }
}
