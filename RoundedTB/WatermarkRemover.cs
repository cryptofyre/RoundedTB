using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RoundedTB
{
    /// <summary>
    /// Removes the Windows Insider / evaluation build watermark from the desktop, a C# port of
    /// UWD2 (https://github.com/machineonamission/uwd2) by Melody.
    ///
    /// The watermark is drawn by <c>CDesktopWatermark::s_DesktopBuildPaint</c> inside shell32.dll.
    /// We find that function's RVA from Microsoft's public symbols, then overwrite its first
    /// instruction in the running Explorer with a return, so the paint routine does nothing.
    ///
    /// Flow (mirrors UWD2):
    ///   1. Find the shell32.dll loaded in the shell's explorer.exe - its base, size, on-disk path
    ///      and the PDB GUID/age of that exact build (read from the loaded image's debug directory).
    ///   2. Resolve the RVA of s_DesktopBuildPaint. Cached per-build; on a miss we download the
    ///      matching shell32.pdb from the Microsoft symbol server and let dbghelp enumerate it.
    ///   3. WriteProcessMemory a return instruction at base + RVA (0xC3 on x64, BR X30 on ARM64).
    ///   4. Refresh the desktop so the (now empty) watermark repaint happens immediately.
    ///
    /// The patch lives in Explorer's memory, so it does NOT persist across an Explorer or system
    /// restart - RoundedTB re-applies it when it detects Explorer coming back. It never touches the
    /// "Activate Windows" watermark, which is drawn by a different code path.
    ///
    /// No elevation is required: Explorer runs as the same user at the same integrity level, so
    /// OpenProcess with VM_WRITE succeeds without admin. WriteProcessMemory flips the code page to
    /// writable for the write and restores its protection afterwards.
    /// </summary>
    internal static class WatermarkRemover
    {
        public readonly struct Result
        {
            public bool Success { get; }
            public bool Changed { get; }
            public string Message { get; }

            public Result(bool success, bool changed, string message)
            {
                Success = success;
                Changed = changed;
                Message = message;
            }

            public static Result Ok(bool changed, string message) => new(true, changed, message);
            public static Result Fail(string message) => new(false, false, message);
        }

        private const string Shell32Name = "shell32.dll";
        private const string TargetSymbol = "s_DesktopBuildPaint";
        private const string SymbolServer = "https://msdl.microsoft.com/download/symbols";

        // dbghelp is single-threaded per process; the worker thread and the UI thread can both ask
        // for a patch (startup, toggle, Explorer restart), so everything here runs under one lock.
        private static readonly object Gate = new();

        // State kept so a disable within the same session can put the original bytes back. Once
        // Explorer restarts this is stale (fresh shell32), so it is keyed by the Explorer PID.
        private static uint patchedPid;
        private static IntPtr patchedAddress;
        private static byte[]? originalBytes;

        /// <summary>The return instruction to write, for the architecture this process is running as.</summary>
        private static byte[]? GetReturnStub()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                case Architecture.X86:
                    return new byte[] { 0xC3 };            // ret
                case Architecture.Arm64:
                    return new byte[] { 0xC0, 0x03, 0x1F, 0xD6 }; // br x30 (return)
                default:
                    return null;
            }
        }

        private static string CacheDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoundedTB", "symbols");

        /// <summary>
        /// Resolves and applies the patch. Safe to call repeatedly: if the function is already a
        /// return it reports success without rewriting. Never throws - failures come back as a
        /// <see cref="Result"/> with a message to log.
        /// </summary>
        public static Result Apply(Action<string>? log = null)
        {
            lock (Gate)
            {
                try
                {
                    return ApplyCore(log ?? (_ => { }));
                }
                catch (Exception ex)
                {
                    return Result.Fail($"Watermark patch failed: {ex.Message}");
                }
            }
        }

        private static Result ApplyCore(Action<string> log)
        {
            byte[]? stub = GetReturnStub();
            if (stub == null)
            {
                return Result.Fail($"Unsupported architecture {RuntimeInformation.ProcessArchitecture}.");
            }

            uint pid = FindShellExplorerPid();
            if (pid == 0)
            {
                return Result.Fail("Couldn't find the desktop explorer.exe.");
            }

            IntPtr hProc = WatermarkInterop.OpenProcess(
                WatermarkInterop.ProcessAccess.QueryInformation | WatermarkInterop.ProcessAccess.VmOperation |
                WatermarkInterop.ProcessAccess.VmRead | WatermarkInterop.ProcessAccess.VmWrite,
                false, pid);
            if (hProc == IntPtr.Zero)
            {
                return Result.Fail($"Couldn't open explorer.exe (pid {pid}): error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                if (!TryGetLoadedShell32(hProc, out ulong baseAddr, out uint size, out string path, out string guidKey))
                {
                    return Result.Fail("Couldn't locate shell32.dll inside explorer.exe.");
                }
                log($"shell32.dll loaded at {baseAddr:X} ({guidKey}).");

                uint rva = GetCachedRva(guidKey);
                if (rva == 0)
                {
                    log("Resolving the watermark symbol from Microsoft's symbol server...");
                    rva = ResolveRva(hProc, baseAddr, size, path, guidKey, log);
                    if (rva == 0)
                    {
                        return Result.Fail($"Couldn't resolve {TargetSymbol} for this build (a pending shell32 update needs a reboot, or symbols are unavailable).");
                    }
                    CacheRva(guidKey, rva);
                }
                log($"{TargetSymbol} is at RVA {rva:X}.");

                IntPtr target = (IntPtr)(baseAddr + rva);

                byte[] current = new byte[stub.Length];
                if (!WatermarkInterop.ReadProcessMemory(hProc, target, current, (IntPtr)current.Length, out _))
                {
                    return Result.Fail($"Couldn't read the watermark function: error {Marshal.GetLastWin32Error()}.");
                }

                if (BytesEqual(current, stub))
                {
                    // Already patched (by us in a previous tick, or still from an earlier apply).
                    patchedPid = pid;
                    patchedAddress = target;
                    Refresh();
                    return Result.Ok(false, "Watermark already removed.");
                }

                // Keep the real original so a disable this session can restore it.
                patchedPid = pid;
                patchedAddress = target;
                originalBytes = current;

                if (!WatermarkInterop.WriteProcessMemory(hProc, target, stub, (IntPtr)stub.Length, out _))
                {
                    return Result.Fail($"Couldn't write the patch: error {Marshal.GetLastWin32Error()}.");
                }
                WatermarkInterop.FlushInstructionCache(hProc, target, (IntPtr)stub.Length);

                Refresh();
                log("Watermark removed.");
                return Result.Ok(true, "Watermark removed.");
            }
            finally
            {
                WatermarkInterop.CloseHandle(hProc);
            }
        }

        /// <summary>
        /// Puts the original instruction back, for a disable within the same session. If Explorer has
        /// restarted since we patched (fresh shell32), there is nothing to undo.
        /// </summary>
        public static Result Restore(Action<string>? log = null)
        {
            Action<string> l = log ?? (_ => { });
            lock (Gate)
            {
                try
                {
                    if (originalBytes == null || patchedAddress == IntPtr.Zero)
                    {
                        return Result.Ok(false, "Nothing to restore.");
                    }

                    uint pid = FindShellExplorerPid();
                    if (pid == 0 || pid != patchedPid)
                    {
                        // Explorer was restarted - the patch is already gone with the old shell32.
                        ClearState();
                        return Result.Ok(false, "Explorer restarted; the watermark will return on its own.");
                    }

                    IntPtr hProc = WatermarkInterop.OpenProcess(
                        WatermarkInterop.ProcessAccess.QueryInformation | WatermarkInterop.ProcessAccess.VmOperation |
                        WatermarkInterop.ProcessAccess.VmRead | WatermarkInterop.ProcessAccess.VmWrite,
                        false, pid);
                    if (hProc == IntPtr.Zero)
                    {
                        return Result.Fail($"Couldn't open explorer.exe to restore: error {Marshal.GetLastWin32Error()}.");
                    }

                    try
                    {
                        if (!WatermarkInterop.WriteProcessMemory(hProc, patchedAddress, originalBytes, (IntPtr)originalBytes.Length, out _))
                        {
                            return Result.Fail($"Couldn't restore the original bytes: error {Marshal.GetLastWin32Error()}.");
                        }
                        WatermarkInterop.FlushInstructionCache(hProc, patchedAddress, (IntPtr)originalBytes.Length);
                        ClearState();
                        Refresh();
                        l("Watermark restored.");
                        return Result.Ok(true, "Watermark restored.");
                    }
                    finally
                    {
                        WatermarkInterop.CloseHandle(hProc);
                    }
                }
                catch (Exception ex)
                {
                    return Result.Fail($"Restoring the watermark failed: {ex.Message}");
                }
            }
        }

        /// <summary>Call when Explorer has (re)started so a previous session's patch is forgotten.</summary>
        public static void ForgetPatch()
        {
            lock (Gate)
            {
                ClearState();
            }
        }

        private static void ClearState()
        {
            patchedPid = 0;
            patchedAddress = IntPtr.Zero;
            originalBytes = null;
        }

        // --- Finding the shell's explorer.exe and its shell32 ---

        /// <summary>
        /// The desktop shell runs as the explorer.exe that owns Progman. Matching by window owner is
        /// exact, unlike matching the exe name (File Explorer windows share the process, and there can
        /// be more than one explorer.exe across sessions).
        /// </summary>
        private static uint FindShellExplorerPid()
        {
            IntPtr progman = WatermarkInterop.FindWindowW("Progman", "Program Manager");
            if (progman == IntPtr.Zero)
            {
                progman = WatermarkInterop.FindWindowW("Shell_TrayWnd", null);
            }
            if (progman == IntPtr.Zero)
            {
                return 0;
            }
            WatermarkInterop.GetWindowThreadProcessId(progman, out uint pid);
            return pid;
        }

        private static bool TryGetLoadedShell32(IntPtr hProc, out ulong baseAddr, out uint size, out string path, out string guidKey)
        {
            baseAddr = 0;
            size = 0;
            path = string.Empty;
            guidKey = string.Empty;

            // Query the needed buffer size first, then enumerate.
            if (!WatermarkInterop.EnumProcessModulesEx(hProc, Array.Empty<IntPtr>(), 0, out uint needed, WatermarkInterop.LIST_MODULES_ALL) && needed == 0)
            {
                return false;
            }
            int count = (int)(needed / IntPtr.Size);
            if (count == 0)
            {
                return false;
            }
            IntPtr[] modules = new IntPtr[count];
            if (!WatermarkInterop.EnumProcessModulesEx(hProc, modules, needed, out needed, WatermarkInterop.LIST_MODULES_ALL))
            {
                return false;
            }
            count = Math.Min(count, (int)(needed / IntPtr.Size));

            StringBuilder nameBuf = new StringBuilder(260);
            foreach (IntPtr hMod in modules)
            {
                nameBuf.Clear();
                if (WatermarkInterop.GetModuleBaseNameW(hProc, hMod, nameBuf, (uint)nameBuf.Capacity) == 0)
                {
                    continue;
                }
                if (!nameBuf.ToString().Equals(Shell32Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                baseAddr = (ulong)hMod.ToInt64();

                StringBuilder pathBuf = new StringBuilder(32768);
                path = WatermarkInterop.GetModuleFileNameExW(hProc, hMod, pathBuf, (uint)pathBuf.Capacity) != 0
                    ? pathBuf.ToString()
                    : @"C:\Windows\System32\shell32.dll";

                if (WatermarkInterop.GetModuleInformation(hProc, hMod, out WatermarkInterop.MODULEINFO mi, (uint)Marshal.SizeOf<WatermarkInterop.MODULEINFO>()))
                {
                    size = mi.SizeOfImage;
                }

                guidKey = ReadPdbGuidKey(hProc, baseAddr);
                return !string.IsNullOrEmpty(guidKey);
            }
            return false;
        }

        /// <summary>
        /// Reads the PDB GUID + age for the shell32 build actually mapped into Explorer, from its
        /// in-memory CodeView (RSDS) debug record, formatted as the symbol server expects
        /// ("{guid:N}{age:X}" upper-case) - the key both for the download URL and the local cache.
        /// </summary>
        private static string ReadPdbGuidKey(IntPtr hProc, ulong baseAddr)
        {
            // DOS header -> e_lfanew
            uint eLfanew = ReadU32(hProc, baseAddr + 0x3C);
            ulong nt = baseAddr + eLfanew;
            // IMAGE_NT_HEADERS: Signature(4) + FileHeader(20); OptionalHeader starts at +24.
            ushort optMagic = ReadU16(hProc, nt + 0x18);
            // DataDirectory offset within the optional header: 0x70 (PE32+) or 0x60 (PE32).
            uint dataDirOffset = optMagic == 0x20B ? 0x70u : 0x60u;
            ulong debugDirEntry = nt + 0x18 + dataDirOffset + 6 * 8; // IMAGE_DIRECTORY_ENTRY_DEBUG = 6
            uint debugRva = ReadU32(hProc, debugDirEntry);
            uint debugSize = ReadU32(hProc, debugDirEntry + 4);
            if (debugRva == 0 || debugSize == 0)
            {
                return string.Empty;
            }

            const int entrySize = 28; // IMAGE_DEBUG_DIRECTORY
            int entries = (int)(debugSize / entrySize);
            for (int i = 0; i < entries; i++)
            {
                ulong entry = baseAddr + debugRva + (ulong)(i * entrySize);
                uint type = ReadU32(hProc, entry + 12);     // Type
                if (type != 2)                               // IMAGE_DEBUG_TYPE_CODEVIEW
                {
                    continue;
                }
                uint sizeOfData = ReadU32(hProc, entry + 16);
                uint addrOfRaw = ReadU32(hProc, entry + 20); // AddressOfRawData (RVA for a loaded image)
                if (addrOfRaw == 0 || sizeOfData < 24)
                {
                    continue;
                }
                byte[] cv = ReadBytes(hProc, baseAddr + addrOfRaw, (int)Math.Min(sizeOfData, 1024));
                if (cv.Length < 24 || BitConverter.ToUInt32(cv, 0) != 0x53445352) // 'RSDS'
                {
                    continue;
                }
                byte[] guidBytes = new byte[16];
                Array.Copy(cv, 4, guidBytes, 0, 16);
                uint age = BitConverter.ToUInt32(cv, 20);
                Guid guid = new Guid(guidBytes);
                return guid.ToString("N").ToUpperInvariant() + age.ToString("X");
            }
            return string.Empty;
        }

        // --- Symbol resolution via a downloaded PDB + dbghelp ---

        private static uint ResolveRva(IntPtr hProc, ulong baseAddr, uint size, string imagePath, string guidKey, Action<string> log)
        {
            string pdbDir = Path.Combine(CacheDir, guidKey);
            string pdbPath = Path.Combine(pdbDir, Shell32Name.Replace(".dll", ".pdb"));
            if (!File.Exists(pdbPath))
            {
                if (!TryDownloadPdb(guidKey, pdbPath, log))
                {
                    return 0;
                }
            }

            // dbghelp reads the on-disk image's debug GUID and matches it against the PDB we placed in
            // the search path. If Explorer's build and the on-disk build differ (a pending update), the
            // match fails and we resolve nothing - which is the safe outcome: no wrong-address write.
            if (!WatermarkInterop.SymInitialize(hProc, pdbDir, false))
            {
                log($"SymInitialize failed: error {Marshal.GetLastWin32Error()}.");
                return 0;
            }
            try
            {
                WatermarkInterop.SymSetOptions(
                    WatermarkInterop.SYMOPT_UNDNAME | WatermarkInterop.SYMOPT_EXACT_SYMBOLS |
                    WatermarkInterop.SYMOPT_DEFERRED_LOADS | WatermarkInterop.SYMOPT_FAIL_CRITICAL_ERRORS |
                    WatermarkInterop.SYMOPT_NO_PROMPTS);

                ulong loaded = WatermarkInterop.SymLoadModuleEx(hProc, IntPtr.Zero, imagePath, null, baseAddr, size, IntPtr.Zero, 0);
                if (loaded == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 0) // 0 means it was already loaded, which is fine
                    {
                        log($"SymLoadModuleEx failed: error {err}.");
                        return 0;
                    }
                    loaded = baseAddr;
                }

                ulong matchAddress = 0;
                ulong fallbackAddress = 0;
                WatermarkInterop.SymEnumSymbolsProc callback = (pSymInfo, _, _) =>
                {
                    WatermarkInterop.SYMBOL_INFO info = Marshal.PtrToStructure<WatermarkInterop.SYMBOL_INFO>(pSymInfo);
                    if (fallbackAddress == 0)
                    {
                        fallbackAddress = info.Address;
                    }
                    if ((info.Flags & WatermarkInterop.SYMFLAG_FUNCTION) != 0)
                    {
                        matchAddress = info.Address;
                        return false; // stop at the first function match
                    }
                    return true;
                };

                WatermarkInterop.SymEnumSymbols(hProc, loaded, "*" + TargetSymbol + "*", callback, IntPtr.Zero);
                GC.KeepAlive(callback);

                ulong address = matchAddress != 0 ? matchAddress : fallbackAddress;
                if (address == 0)
                {
                    return 0;
                }
                return (uint)(address - baseAddr);
            }
            finally
            {
                WatermarkInterop.SymUnloadModule64(hProc, baseAddr);
                WatermarkInterop.SymCleanup(hProc);
            }
        }

        private static bool TryDownloadPdb(string guidKey, string pdbPath, Action<string> log)
        {
            string url = $"{SymbolServer}/{Shell32Name.Replace(".dll", ".pdb")}/{guidKey}/{Shell32Name.Replace(".dll", ".pdb")}";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pdbPath)!);
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                // The Microsoft symbol server expects a symbol-store style User-Agent.
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Microsoft-Symbol-Server/10.0.0.0");
                using HttpResponseMessage resp = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    log($"Symbol download returned {(int)resp.StatusCode}.");
                    return false;
                }
                string tmp = pdbPath + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
                }
                if (File.Exists(pdbPath))
                {
                    File.Delete(pdbPath);
                }
                File.Move(tmp, pdbPath);
                log("Symbols downloaded.");
                return true;
            }
            catch (Exception ex)
            {
                log($"Symbol download failed: {ex.Message}");
                return false;
            }
        }

        // --- RVA cache (a 4-byte file per build, like UWD2's .rva cache) ---

        private static uint GetCachedRva(string guidKey)
        {
            try
            {
                string file = Path.Combine(CacheDir, guidKey + ".rva");
                if (!File.Exists(file))
                {
                    return 0;
                }
                byte[] bytes = File.ReadAllBytes(file);
                return bytes.Length == 4 ? BitConverter.ToUInt32(bytes, 0) : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static void CacheRva(string guidKey, uint rva)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllBytes(Path.Combine(CacheDir, guidKey + ".rva"), BitConverter.GetBytes(rva));
            }
            catch
            {
                // A missing cache only costs a re-resolve next time; not worth surfacing.
            }
        }

        // --- Desktop refresh (ported from UWD2's inject::refresh) ---

        private static void Refresh()
        {
            IntPtr progman = WatermarkInterop.FindWindowW("Progman", "Program Manager");
            if (progman == IntPtr.Zero)
            {
                return;
            }
            IntPtr defView = WatermarkInterop.GetWindow(progman, WatermarkInterop.GW_CHILD);

            bool iconsVisible = false;
            if (defView != IntPtr.Zero)
            {
                IntPtr listView = WatermarkInterop.GetWindow(defView, WatermarkInterop.GW_CHILD);
                if (listView != IntPtr.Zero)
                {
                    WatermarkInterop.WINDOWINFO wi = new WatermarkInterop.WINDOWINFO
                    {
                        cbSize = (uint)Marshal.SizeOf<WatermarkInterop.WINDOWINFO>(),
                    };
                    if (WatermarkInterop.GetWindowInfo(listView, ref wi))
                    {
                        iconsVisible = (wi.dwStyle & WatermarkInterop.WS_VISIBLE) == WatermarkInterop.WS_VISIBLE;
                    }
                }
            }

            if (iconsVisible || defView == IntPtr.Zero)
            {
                // Pretending a file association changed makes the shell redraw the desktop, which
                // repaints the (now empty) watermark. Odd, but it is what UWD2 relies on.
                WatermarkInterop.SHChangeNotify(WatermarkInterop.SHCNE_ASSOCCHANGED, WatermarkInterop.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                // Desktop icons are hidden: a refresh won't repaint, so toggle them off and back on.
                WatermarkInterop.SendMessageW(defView, WatermarkInterop.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
                WatermarkInterop.SendMessageW(defView, WatermarkInterop.WM_COMMAND, (IntPtr)0x7402, IntPtr.Zero);
            }
        }

        // --- small memory helpers ---

        private static byte[] ReadBytes(IntPtr hProc, ulong address, int length)
        {
            byte[] buf = new byte[length];
            WatermarkInterop.ReadProcessMemory(hProc, (IntPtr)address, buf, (IntPtr)length, out IntPtr read);
            if ((int)read != length)
            {
                byte[] trimmed = new byte[(int)read];
                Array.Copy(buf, trimmed, (int)read);
                return trimmed;
            }
            return buf;
        }

        private static uint ReadU32(IntPtr hProc, ulong address)
        {
            byte[] b = ReadBytes(hProc, address, 4);
            return b.Length == 4 ? BitConverter.ToUInt32(b, 0) : 0;
        }

        private static ushort ReadU16(IntPtr hProc, ulong address)
        {
            byte[] b = ReadBytes(hProc, address, 2);
            return b.Length == 2 ? BitConverter.ToUInt16(b, 0) : (ushort)0;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
