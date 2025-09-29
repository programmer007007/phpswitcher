using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhpSwitcher;

internal static class Program
{
    private const string RootEnvironmentVariable = "PHPSWITCHER_ROOT";
    private const string PathVariableName = "Path";

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static int Main(string[] args)
    {
        if (!CommandLineOptions.TryParse(args, out CommandLineOptions? options, out string? error))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(error);
            Console.ResetColor();
            return 1;
        }

        if (options!.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Warning: PHP switcher is designed for Windows. The PATH variable will not be updated on this platform.");
            Console.ResetColor();
        }

        RootResolutionResult rootResolution;
        try
        {
            rootResolution = RootResolver.Resolve(options.RootFromArguments);
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.WriteLine(ex.Message);
            return 1;
        }

        string root = rootResolution.Path;
        Environment.SetEnvironmentVariable(RootEnvironmentVariable, root, EnvironmentVariableTarget.Process);
        if (!rootResolution.FromArguments && !rootResolution.FromEnvironmentVariable)
        {
            MaybePersistRoot(root);
        }

        IReadOnlyList<PhpInstallation> installations = InstallationFinder.Find(root).ToList();
        if (installations.Count == 0)
        {
            Console.WriteLine($"No PHP installations were found under '{root}'.");
            return 1;
        }

        return options.Command switch
        {
            CommandKind.List => ListInstallations(root, installations),
            CommandKind.Use => SwitchInstallation(root, installations, options.CommandArgument!),
            _ => RunInteractive(root, installations),
        };
    }

    private static int ListInstallations(string root, IReadOnlyList<PhpInstallation> installations)
    {
        Console.WriteLine($"PHP installations in '{root}':");
        foreach (PhpInstallation installation in installations)
        {
            Console.WriteLine($" - {installation.Name} ({installation.Directory})");
        }

        return 0;
    }

    private static int SwitchInstallation(string root, IReadOnlyList<PhpInstallation> installations, string requestedName)
    {
        PhpInstallation? match = InstallationSelector.TryResolve(requestedName, installations);
        if (match is null)
        {
            Console.WriteLine($"Unable to find a PHP installation matching '{requestedName}'.");
            return 1;
        }

        PathUpdater.Apply(root, match.Directory);
        Console.WriteLine($"Switched PHP version to '{match.Name}'.");
        return 0;
    }

    private static int RunInteractive(string root, IReadOnlyList<PhpInstallation> installations)
    {
        Console.WriteLine($"Available PHP installations in '{root}':");
        for (int index = 0; index < installations.Count; index++)
        {
            PhpInstallation installation = installations[index];
            Console.WriteLine($"  {index + 1}. {installation.Name} -> {installation.Directory}");
        }

        Console.Write("Select a PHP version by number or name: ");
        string? selection = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(selection))
        {
            Console.WriteLine("No selection made. Exiting.");
            return 1;
        }

        PhpInstallation? chosen = InstallationSelector.TryResolve(selection.Trim(), installations);
        if (chosen is null)
        {
            Console.WriteLine($"Unable to find a PHP installation matching '{selection}'.");
            return 1;
        }

        PathUpdater.Apply(root, chosen.Directory);
        Console.WriteLine($"Switched PHP version to '{chosen.Name}'.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PHP Switcher");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  phpswitcher [--root <path>]                 # Interactive mode");
        Console.WriteLine("  phpswitcher list [--root <path>]            # List available PHP versions");
        Console.WriteLine("  phpswitcher use <name> [--root <path>]      # Switch directly to a PHP version");
        Console.WriteLine();
        Console.WriteLine($"You can also define the environment variable {RootEnvironmentVariable} to avoid re-entering the root directory.");
    }

    private sealed record CommandLineOptions(string? RootFromArguments, CommandKind Command, string? CommandArgument, bool ShowHelp)
    {
        public static bool TryParse(string[] args, out CommandLineOptions? options, out string? error)
        {
            string? rootDirectory = null;
            string? command = null;
            string? commandArgument = null;
            bool helpRequested = false;

            var remaining = new List<string>();

            for (int index = 0; index < args.Length; index++)
            {
                string current = args[index];
                switch (current)
                {
                    case "--root":
                    case "-r":
                        if (index + 1 >= args.Length)
                        {
                            error = "The --root option requires a value.";
                            options = null;
                            return false;
                        }

                        rootDirectory = args[++index];
                        break;
                    case "--help":
                    case "-h":
                    case "help":
                        helpRequested = true;
                        break;
                    default:
                        remaining.Add(current);
                        break;
                }
            }

            if (remaining.Count > 0)
            {
                command = remaining[0];
                if (string.Equals(command, "use", StringComparison.OrdinalIgnoreCase))
                {
                    if (remaining.Count < 2)
                    {
                        error = "The 'use' command requires the name of a PHP installation, e.g. 'phpswitcher use php8.2'.";
                        options = null;
                        return false;
                    }

                    commandArgument = remaining[1];
                }
                else if (!string.Equals(command, "list", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Unknown command '{command}'.";
                    options = null;
                    return false;
                }
            }

            options = new CommandLineOptions(rootDirectory, ParseCommand(command), commandArgument, helpRequested);
            error = null;
            return true;
        }

        private static CommandKind ParseCommand(string? command) => command?.ToLowerInvariant() switch
        {
            "list" => CommandKind.List,
            "use" => CommandKind.Use,
            _ => CommandKind.Interactive,
        };
    }

    private enum CommandKind
    {
        Interactive,
        List,
        Use,
    }

    private static class RootResolver
    {
        public static RootResolutionResult Resolve(string? rootFromArguments)
        {
            if (!string.IsNullOrWhiteSpace(rootFromArguments))
            {
                string argumentRoot = rootFromArguments.Trim();
                if (TryValidateDirectory(argumentRoot, out string? normalized))
                {
                    return new RootResolutionResult(normalized!, fromArguments: true, fromEnvironmentVariable: false);
                }

                throw new DirectoryNotFoundException($"The directory '{argumentRoot}' does not exist.");
            }

            foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User })
            {
                string? candidate = Environment.GetEnvironmentVariable(RootEnvironmentVariable, target);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (TryValidateDirectory(candidate, out string? normalized))
                {
                    return new RootResolutionResult(normalized!, fromArguments: false, fromEnvironmentVariable: true);
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"The environment variable {RootEnvironmentVariable} points to '{candidate}', which does not exist.");
                Console.ResetColor();
            }

            return PromptForRoot();
        }

        private static RootResolutionResult PromptForRoot()
        {
            while (true)
            {
                Console.Write("Enter the root directory that contains PHP installations (or 'q' to quit): ");
                string? input = Console.ReadLine();
                if (input is null)
                {
                    throw new OperationCanceledException("No input received. Aborting.");
                }

                input = input.Trim();
                if (string.Equals(input, "q", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "quit", StringComparison.OrdinalIgnoreCase))
                {
                    throw new OperationCanceledException("Operation cancelled by user.");
                }

                if (string.IsNullOrWhiteSpace(input))
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("No directory entered. Please try again.");
                    Console.ResetColor();
                    continue;
                }

                string sanitized = input.Trim('\"');
                if (TryValidateDirectory(sanitized, out string? normalized))
                {
                    return new RootResolutionResult(normalized!, fromArguments: false, fromEnvironmentVariable: false);
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"The directory '{sanitized}' does not exist. Please try again.");
                Console.ResetColor();
            }
        }

        private static bool TryValidateDirectory(string candidate, out string? fullPath)
        {
            if (!TryNormalizePath(candidate, out string? normalized) || normalized is null)
            {
                fullPath = null;
                return false;
            }

            if (!Directory.Exists(normalized))
            {
                fullPath = null;
                return false;
            }

            fullPath = normalized;
            return true;
        }
    }

    private static void MaybePersistRoot(string root)
    {
        Console.Write($"Remember this root in the {RootEnvironmentVariable} user environment variable? [y/N]: ");
        string? response = Console.ReadLine();
        if (!IsAffirmative(response))
        {
            return;
        }

        try
        {
            Environment.SetEnvironmentVariable(RootEnvironmentVariable, root, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(RootEnvironmentVariable, root, EnvironmentVariableTarget.Process);
            Console.WriteLine($"Stored '{root}' in {RootEnvironmentVariable}. Future runs will use it automatically.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Unable to persist the root directory: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static class InstallationFinder
    {
        public static IEnumerable<PhpInstallation> Find(string rootDirectory)
        {
            if (!Directory.Exists(rootDirectory))
            {
                throw new DirectoryNotFoundException($"The directory '{rootDirectory}' does not exist.");
            }

            foreach (string directory in Directory.EnumerateDirectories(rootDirectory))
            {
                string phpExecutable = Path.Combine(directory, "php.exe");
                if (!File.Exists(phpExecutable))
                {
                    continue;
                }

                string name = GetDirectoryName(directory);
                yield return new PhpInstallation(name, directory);
            }
        }
    }

    private static class InstallationSelector
    {
        public static PhpInstallation? TryResolve(string selection, IReadOnlyList<PhpInstallation> installations)
        {
            if (int.TryParse(selection, out int numericIndex))
            {
                numericIndex -= 1;
                if (numericIndex >= 0 && numericIndex < installations.Count)
                {
                    return installations[numericIndex];
                }
            }

            return installations.FirstOrDefault(installation =>
                string.Equals(installation.Name, selection, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static class PathUpdater
    {
        public static void Apply(string rootDirectory, string selectedDirectory)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.WriteLine($"Selected PHP directory: {selectedDirectory}");
                return;
            }

            string existingUserPath = Environment.GetEnvironmentVariable(PathVariableName, EnvironmentVariableTarget.User) ?? string.Empty;
            string updatedPath = BuildUpdatedPath(existingUserPath, rootDirectory, selectedDirectory);

            Environment.SetEnvironmentVariable(PathVariableName, updatedPath, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(PathVariableName, updatedPath, EnvironmentVariableTarget.Process);

            Console.WriteLine("PATH updated for the current user. You may need to open a new terminal for the change to take effect.");
        }

        private static string BuildUpdatedPath(string existingPath, string rootDirectory, string selectedDirectory)
        {
            var entries = existingPath
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToList();

            string? normalizedRoot = TryNormalizePath(rootDirectory, out string? normalizedRootValue)
                ? normalizedRootValue
                : null;
            string? normalizedSelection = TryNormalizePath(selectedDirectory, out string? normalizedSelectionValue)
                ? normalizedSelectionValue
                : null;

            if (normalizedRoot is not null)
            {
                entries = entries
                    .Where(entry => !IsPathUnderRoot(entry, normalizedRoot) ||
                                    (normalizedSelection is not null && IsSamePath(entry, normalizedSelection)))
                    .ToList();
            }

            if (normalizedSelection is not null)
            {
                entries = entries
                    .Where(entry => !IsSamePath(entry, normalizedSelection))
                    .ToList();
                entries.Insert(0, normalizedSelection);
            }
            else
            {
                entries.Insert(0, selectedDirectory);
            }

            return string.Join(';', entries);
        }

        private static bool IsPathUnderRoot(string path, string normalizedRoot)
        {
            if (!TryNormalizePath(path, out string? normalizedPath) || normalizedPath is null)
            {
                return false;
            }

            if (!normalizedPath.StartsWith(normalizedRoot, PathComparer))
            {
                return false;
            }

            if (normalizedPath.Length == normalizedRoot.Length)
            {
                return true;
            }

            char separator = normalizedPath[normalizedRoot.Length];
            return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
        }

        private static bool IsSamePath(string left, string right)
        {
            if (!TryNormalizePath(left, out string? normalizedLeft) ||
                !TryNormalizePath(right, out string? normalizedRight) ||
                normalizedLeft is null ||
                normalizedRight is null)
            {
                return string.Equals(left, right, PathComparer);
            }

            return PathComparer.Equals(normalizedLeft, normalizedRight);
        }
    }

    private static string GetDirectoryName(string directory)
    {
        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? directory : name;
    }

    private static bool TryNormalizePath(string path, out string? normalized)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                normalized = null;
                return false;
            }

            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception)
        {
            normalized = null;
            return false;
        }
    }

    private static bool IsAffirmative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return string.Equals(trimmed, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record PhpInstallation(string Name, string Directory);

    private sealed record RootResolutionResult(string Path, bool FromArguments, bool FromEnvironmentVariable);
}
