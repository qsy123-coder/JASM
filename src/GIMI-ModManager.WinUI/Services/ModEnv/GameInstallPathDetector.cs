using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModEnv;

/// <summary>Result of game install-location detection.</summary>
public record GameInstallInfo(string InstallDir)
{
    /// <summary>Drive root of the install, e.g. "D:\". Used as the default home for the XXMI folder.</summary>
    public string DriveRoot => Path.GetPathRoot(InstallDir) ?? string.Empty;
}

/// <summary>
/// Best-effort detection of the Wuthering Waves install location, via registry uninstall keys
/// and common install paths. Returns null when the game could not be located (caller should
/// fall back to a manual folder picker).
/// </summary>
public class GameInstallPathDetector
{
    private readonly ILogger _logger;

    private static readonly string[] GameNames = { "Wuthering Waves", "鸣潮" };

    private static readonly string[] CandidateExecutables =
    {
        "Wuthering Waves.exe",
        "Launcher.exe",
        "Client.exe"
    };

    public GameInstallPathDetector(ILogger logger)
    {
        _logger = logger.ForContext<GameInstallPathDetector>();
    }

    public Task<GameInstallInfo?> DetectAsync(CancellationToken ct = default)
        => Task.FromResult(Detect());

    private GameInstallInfo? Detect()
    {
        try
        {
            var fromRegistry = FindInRegistry();
            if (fromRegistry is not null)
            {
                _logger.Information("Detected Wuthering Waves install at {Path} via registry", fromRegistry);
                return new GameInstallInfo(fromRegistry);
            }

            var fromCommonPaths = FindInCommonPaths();
            if (fromCommonPaths is not null)
            {
                _logger.Information("Detected Wuthering Waves install at {Path} via common path scan", fromCommonPaths);
                return new GameInstallInfo(fromCommonPaths);
            }

            var fromDriveRoots = FindInDriveRoots();
            if (fromDriveRoots is not null)
            {
                _logger.Information("Detected Wuthering Waves install at {Path} via drive root scan", fromDriveRoots);
                return new GameInstallInfo(fromDriveRoots);
            }

            _logger.Warning("Could not auto-detect Wuthering Waves install location");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Game install path detection failed");
            return null;
        }
    }

    /// <summary>Best-effort game version, from the game executable's file version info.</summary>
    public string? GetGameVersion(string installDir)
    {
        try
        {
            var exe = FindGameExecutable(installDir);
            if (exe is null) return null;

            var fvi = FileVersionInfo.GetVersionInfo(exe);
            var version = fvi.ProductVersion ?? fvi.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to read Wuthering Waves version from {InstallDir}", installDir);
            return null;
        }
    }

    private string? FindGameExecutable(string installDir)
    {
        var targets = new[]
        {
            Path.Combine(installDir, "Wuthering Waves.exe"),
            Path.Combine(installDir, "Client.exe"),
            Path.Combine(installDir, "Client", "Binaries", "Win64", "Wuthering Waves.exe"),
            Path.Combine(installDir, "Wuthering Waves Game", "Client", "Binaries", "Win64", "Wuthering Waves.exe"),
            Path.Combine(installDir, "Client", "Binaries", "Win64", "Client.exe")
        };

        return targets.FirstOrDefault(File.Exists);
    }

    private string? FindInRegistry()
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                var found = ScanUninstallKey(baseKey, uninstallPath);
                if (found is not null) return found;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to scan LM uninstall registry ({View})", view);
            }
        }

        try
        {
            using var cu = Registry.CurrentUser;
            var found = ScanUninstallKey(cu, uninstallPath);
            if (found is not null) return found;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to scan CU uninstall registry");
        }

        return null;
    }

    private string? ScanUninstallKey(RegistryKey root, string uninstallPath)
    {
        using var uninstall = root.OpenSubKey(uninstallPath);
        if (uninstall is null) return null;

        foreach (var subKeyName in uninstall.GetSubKeyNames())
        {
            try
            {
                using var sub = uninstall.OpenSubKey(subKeyName);
                if (sub is null) continue;

                var displayName = sub.GetValue("DisplayName") as string;
                var displayIcon = sub.GetValue("DisplayIcon") as string;
                if (!MatchesGame(displayName) && !MatchesGame(displayIcon)) continue;

                var installLocation = sub.GetValue("InstallLocation") as string;
                if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
                    return installLocation;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to inspect uninstall key {Key}", subKeyName);
            }
        }

        return null;
    }

    private string? FindInCommonPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new[]
        {
            Path.Combine(programFiles, "Wuthering Waves"),
            Path.Combine(programFiles, "Kuro Game", "Wuthering Waves"),
            Path.Combine(programFiles, "KuroGames", "Wuthering Waves"),
            Path.Combine(programFiles, "Epic Games", "Wuthering Waves"),
            Path.Combine(programFilesX86, "Wuthering Waves"),
            Path.Combine(programFilesX86, "Kuro Game", "Wuthering Waves"),
            Path.Combine(programFilesX86, "KuroGames", "Wuthering Waves"),
            Path.Combine(programFilesX86, "Epic Games", "Wuthering Waves")
        };

        foreach (var candidate in candidates)
        {
            if (LooksLikeGameInstall(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Scans each fixed drive's root for common Wuthering Waves folder names. Many players install
    /// to a custom drive root (e.g. D:\Wuthering Waves) rather than Program Files, and the game is
    /// often absent from the registry uninstall keys, so this catches those installs.
    /// </summary>
    private string? FindInDriveRoots()
    {
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                    continue;

                var root = drive.RootDirectory.FullName;
                var candidates = new[]
                {
                    Path.Combine(root, "Wuthering Waves"),
                    Path.Combine(root, "Wuthering Waves Game"),
                    Path.Combine(root, "Kuro Games", "Wuthering Waves"),
                    Path.Combine(root, "KuroGames", "Wuthering Waves")
                };

                foreach (var candidate in candidates)
                {
                    if (LooksLikeGameInstall(candidate))
                        return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to scan drive roots for Wuthering Waves install");
        }

        return null;
    }

    private static bool LooksLikeGameInstall(string dir)
    {
        if (!Directory.Exists(dir)) return false;

        foreach (var file in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
        {
            if (CandidateExecutables.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                return true;
        }

        var clientDir = Path.Combine(dir, "Client");
        if (Directory.Exists(clientDir) && Directory.GetFiles(clientDir, "*.exe", SearchOption.AllDirectories).Any())
            return true;

        return false;
    }

    private static bool MatchesGame(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return GameNames.Any(name => value.Contains(name, StringComparison.OrdinalIgnoreCase));
    }
}
