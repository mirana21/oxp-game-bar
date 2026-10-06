param([Parameter(Mandatory=$true)][string]$Path, [string[]]$ForbiddenText = @())
$ErrorActionPreference = 'Stop'
# Values are read from the build machine, never saved in the release or report.
$privateValues = @($env:USERNAME, $env:COMPUTERNAME, [Security.Principal.WindowsIdentity]::GetCurrent().Name,
    [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) + $ForbiddenText
$projectRoot = Split-Path $PSScriptRoot -Parent
if (Get-Command git -ErrorAction SilentlyContinue) {
    $privateValues += @(& git -C $projectRoot config user.name; & git -C $projectRoot config user.email)
}
$privateValues = @($privateValues | Where-Object { $_ -and $_.Length -ge 4 } | Select-Object -Unique)
if (-not ('Oxp3ReleasePrivacy' -as [type])) {
    $privacySource = @'
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
public static class Oxp3ReleasePrivacy {
    private static readonly Regex ProfilePath = new Regex(@"(?i)(?:[a-z]:[\\/]Users[\\/]|/(?:Users|home)/)[^\\/\s\x00]+", RegexOptions.Compiled);
    private static readonly Regex UserSid = new Regex(@"S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+", RegexOptions.Compiled);
    private static readonly Encoding[] Encodings = { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode };
    private static readonly string[] PrivateFiles = { "remembered-watts.json", "preferences-backup.json", "startup-receipt.json", "OneXConsole.original.xml", "bridge-token.txt" };
    public static int Check(string path, string[] forbidden) {
        using (var file = File.OpenRead(path)) return Archive(file, Path.GetFileName(path), forbidden, 0);
    }
    private static int Archive(Stream stream, string label, string[] forbidden, int depth) {
        if (depth > 4) throw new Exception("Release archive nesting exceeds the inspection limit.");
        int count = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true)) {
            foreach (var entry in zip.Entries) {
                if (String.IsNullOrEmpty(entry.Name)) continue;
                string name = label + "!" + entry.FullName;
                CheckText(name, name, forbidden);
                string extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extension == ".pdb" || extension == ".appxsym" || extension == ".pfx" || extension == ".p12" || extension == ".key" || extension == ".pem" || extension == ".log" ||
                    entry.Name.StartsWith("setup-", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith("-result.txt", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Development/private file in release: " + name);
                foreach (string privateFile in PrivateFiles)
                    if (entry.Name.Equals(privateFile, StringComparison.OrdinalIgnoreCase)) throw new Exception("Machine settings in release: " + name);
                if (entry.Length > 512L * 1024 * 1024) throw new Exception("Release entry exceeds the inspection limit: " + name);
                using (var input = entry.Open()) {
                    if (extension == ".zip" || extension == ".msix" || extension == ".appx") {
                        using (var memory = new MemoryStream()) { input.CopyTo(memory); memory.Position = 0; count += Archive(memory, name, forbidden, depth + 1); }
                    } else { Scan(input, name, forbidden); count++; }
                }
            }
        }
        return count;
    }
    private static void CheckText(string content, string name, string[] forbidden) {
        if (ProfilePath.IsMatch(content)) throw new Exception("Personal profile path in release: " + name);
        if (UserSid.IsMatch(content)) throw new Exception("Windows account SID in release: " + name);
        foreach (string value in forbidden) {
            // A machine called "ONEX" must not reject the product name
            // "ONEXConsole". Match account/host tokens, not word fragments.
            if (content.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 &&
                Regex.IsMatch(content, @"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase))
                throw new Exception("Build-machine identity in release: " + name);
        }
    }
    private static void Scan(Stream input, string name, string[] forbidden) {
        // Overlap detects strings crossing chunk boundaries. Both UTF-16 byte
        // alignments are inspected because PE sections need not be aligned.
        int overlap = 4096;
        foreach (string value in forbidden) overlap = Math.Max(overlap, value.Length * 4 + 8);
        byte[] buffer = new byte[1024 * 1024 + overlap];
        int kept = 0, read;
        while ((read = input.Read(buffer, kept, buffer.Length - kept)) > 0) {
            int length = kept + read;
            foreach (var encoding in Encodings) {
                CheckText(encoding.GetString(buffer, 0, length), name, forbidden);
                if (encoding != Encoding.UTF8 && length > 1) CheckText(encoding.GetString(buffer, 1, length - 1), name, forbidden);
            }
            kept = Math.Min(overlap, length);
            Buffer.BlockCopy(buffer, length - kept, buffer, 0, kept);
        }
    }
}
'@
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        Add-Type -TypeDefinition $privacySource -ReferencedAssemblies System.dll,System.Core.dll,System.IO.Compression.dll,System.IO.Compression.FileSystem.dll
    } else { Add-Type -TypeDefinition $privacySource }
}
$count = [Oxp3ReleasePrivacy]::Check((Resolve-Path -LiteralPath $Path).Path, $privateValues)
Write-Output ('PASS: release privacy scan inspected ' + $count + ' files, including nested MSIX/APPX binaries; no build-machine identity, personal paths, account SIDs, debug symbols or saved settings found.')
