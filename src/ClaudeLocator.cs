using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ClaudeUsageMonitor;

/// <summary>Finds the official Claude Code executable and refuses anything not signed by Anthropic.</summary>
internal static class ClaudeLocator
{
    const string Publisher = "Anthropic, PBC";

    static readonly object Gate = new();
    static (string Path, long Length, DateTime WriteUtc)? verified;

    public static IEnumerable<string> Candidates(string? configured)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            list.Add(configured.Trim().Trim('"'));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        list.Add(Path.Combine(profile, ".local", "bin", "claude.exe"));
        list.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe"));
        list.AddRange(SafeEnumerate(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "Anthropic.ClaudeCode_*")
            .Select(dir => Path.Combine(dir, "claude.exe")));
        list.Add(Path.Combine(roaming, "npm", "node_modules", "@anthropic-ai", "claude-code-win32-x64", "claude.exe"));
        list.Add(Path.Combine(roaming, "npm", "node_modules", "@anthropic-ai", "claude-code", "node_modules", "@anthropic-ai", "claude-code-win32-x64", "claude.exe"));

        string here = AppContext.BaseDirectory.TrimEnd('\\');
        string cwd = Environment.CurrentDirectory.TrimEnd('\\');
        foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            string dir = raw.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(dir))
                continue;
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            // Never pick up a claude.exe planted next to this program or in the current directory.
            if (full.Equals(here, StringComparison.OrdinalIgnoreCase) || full.Equals(cwd, StringComparison.OrdinalIgnoreCase))
                continue;
            list.Add(Path.Combine(full, "claude.exe"));
        }

        return list.Where(File.Exists).Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the first candidate that carries a valid Anthropic signature. A configured file that
    /// exists but fails the check is refused outright rather than silently replaced; a configured
    /// file that no longer exists falls back to automatic discovery.
    /// </summary>
    public static string FindVerified(string? configured)
    {
        string chosen = (configured ?? "").Trim().Trim('"');
        if (chosen.Length > 0 && File.Exists(chosen))
            return IsVerified(Resolve(chosen)) ? Resolve(chosen) : throw new MonitorException("SIGNATURE_INVALID");

        bool sawCandidate = false;
        foreach (string path in Candidates(configured))
        {
            sawCandidate = true;
            if (IsVerified(path))
                return path;
        }
        throw new MonitorException(sawCandidate ? "SIGNATURE_INVALID" : "CLAUDE_NOT_FOUND");
    }

    public static bool IsVerified(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return false;
        var stamp = (info.FullName, info.Length, info.LastWriteTimeUtc);
        lock (Gate)
        {
            if (verified == stamp)
                return true;
        }
        if (!Authenticode.IsTrusted(info.FullName) || !SignedBy(info.FullName, Publisher))
            return false;
        lock (Gate)
            verified = stamp;
        return true;
    }

    static bool SignedBy(string path, string organization)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false) == organization
                || cert.Subject.Contains("O=\"" + organization + "\"", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static string Resolve(string path)
    {
        try
        {
            return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : [];
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }
}

/// <summary>WinVerifyTrust check: the file's Authenticode signature chains to a trusted root and the hash matches.</summary>
internal static class Authenticode
{
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsTrusted(string path)
    {
        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = pathPtr,
        };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,           // WTD_UI_NONE
                fdwRevocationChecks = 0,  // WTD_REVOKE_NONE: no network round-trip
                dwUnionChoice = 1,        // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 1,        // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000,     // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            Guid action = GenericVerifyV2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            data.dwStateAction = 2;       // WTD_STATEACTION_CLOSE
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
