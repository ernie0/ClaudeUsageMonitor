using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ClaudeUsageMonitor;

/// <summary>Finds the official Claude Code executable and refuses anything not signed by Anthropic.</summary>
internal static class ClaudeLocator
{
    const string Publisher = "Anthropic, PBC";

    static readonly object Gate = new();
    static (string Path, long Length, DateTime WriteUtc)? verified;

    /// <summary>A non-blocking note about the signature check (macOS only), shown in the details window.</summary>
    public static string Warning { get; private set; } = "";

    public static IEnumerable<string> Candidates(string? configured) =>
        OperatingSystem.IsMacOS() ? MacCandidates(configured) : WindowsCandidates(configured);

    /// <summary>
    /// Apps started from Finder do not get the shell's PATH, so the usual install places are listed
    /// explicitly: the native installer, the old local install, Homebrew on Apple Silicon and Intel.
    /// </summary>
    static IEnumerable<string> MacCandidates(string? configured)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            list.Add(configured.Trim().Trim('"'));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        list.Add(Path.Combine(home, ".local", "bin", "claude"));
        list.Add(Path.Combine(home, ".claude", "local", "claude"));
        list.Add("/opt/homebrew/bin/claude");
        list.Add("/usr/local/bin/claude");

        string here = AppContext.BaseDirectory.TrimEnd('/');
        string cwd = Environment.CurrentDirectory.TrimEnd('/');
        foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
        {
            string dir = raw.Trim();
            if (!Path.IsPathFullyQualified(dir))
                continue;
            string full = Path.GetFullPath(dir).TrimEnd('/');
            if (full == here || full == cwd)
                continue;
            list.Add(Path.Combine(full, "claude"));
        }
        // npm installs a Node script, not a native program; only native binaries can be checked and run.
        return list.Where(File.Exists).Select(Resolve).Where(MacCodeSign.IsMachO).Distinct(StringComparer.Ordinal);
    }

    static IEnumerable<string> WindowsCandidates(string? configured)
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
        if (OperatingSystem.IsWindows())
        {
            if (!Authenticode.IsTrusted(info.FullName) || !SignedBy(info.FullName, Publisher))
                return false;
        }
        else if (OperatingSystem.IsMacOS())
        {
            var (trusted, warning) = MacCodeSign.Check(info.FullName);
            if (!trusted)
                return false;
            Warning = warning;
        }
        else
        {
            return false;
        }
        lock (Gate)
            verified = stamp;
        return true;
    }

    static bool SignedBy(string path, string organization)
    {
        if (!OperatingSystem.IsWindows())
            return false;
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

/// <summary>
/// macOS check with the system's codesign tool. Strict: an Apple-issued Developer ID certificate whose
/// team is Anthropic's, with an intact signature. Users report that some Claude Code builds fail the
/// integrity part on macOS 26, so (by the user's decision) a binary whose signature still names
/// Anthropic's team is accepted with a visible warning instead of being blocked.
/// </summary>
internal static class MacCodeSign
{
    /// <summary>Anthropic's Apple developer team, as shown by codesign on Claude Code builds.</summary>
    public const string AnthropicTeam = "Q6L2SF6YDW";

    const string Codesign = "/usr/bin/codesign";

    public static (bool Trusted, string Warning) Check(string path)
    {
        string requirement = $"anchor apple generic and certificate leaf[subject.OU] = \"{AnthropicTeam}\"";
        if (Run(["--verify", "--strict", "-R=" + requirement, path]).ExitCode == 0)
            return (true, "");

        var (_, details) = Run(["-dv", "--verbose=2", path]);
        bool namedAnthropic = details.Split('\n').Any(line => line.Trim() == "TeamIdentifier=" + AnthropicTeam);
        return namedAnthropic
            ? (true, "claude 的簽章完整性檢查沒有通過（macOS 26 已知狀況），已依簽章上的 Anthropic 開發者編號確認來源。")
            : (false, "");
    }

    /// <summary>Native Mach-O program (thin or universal); npm's Node script is not one.</summary>
    public static bool IsMachO(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (file.Read(magic) < 4)
                return false;
            uint value = BitConverter.ToUInt32(magic);
            return value is 0xFEEDFACF or 0xFEEDFACE or 0xBEBAFECA or 0xCAFEBABE;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static (int ExitCode, string Output) Run(string[] arguments)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(Codesign)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info);
            if (process == null)
                return (-1, "");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "");
            }
            // codesign prints its details on stderr.
            return (process.ExitCode, stdout.Result + "\n" + stderr.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
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
