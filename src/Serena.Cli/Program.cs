// Serena.Cli - Command-line interface entry point
// Project setup, MCP server management, and configuration

using System.CommandLine;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Config;
using Serena.Core.Project;
using Serena.Lsp;
using Serena.Lsp.LanguageServers;

namespace Serena.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand { Description = "Serena - .NET MCP coding agent" };
        rootCommand.Add(CreateServeCommand());
        rootCommand.Add(CreateConfigCommand());
        rootCommand.Add(CreateRegisterCommand());
        rootCommand.Add(CreateListProjectsCommand());
        rootCommand.Add(CreateSetupCommand());
        rootCommand.Add(CreateDoctorCommand());
        rootCommand.Add(CreateVersionCommand());
        rootCommand.Add(CreateProjectCommand());

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    private static Command CreateServeCommand()
    {
        var projectOption = new Option<string?>("--project") { Description = "Project name or path to activate" };
        var command = new Command("serve") { Description = "Start the MCP server over stdio" };
        command.Add(projectOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            string? project = parseResult.GetValue(projectOption);
            var mcpArgs = project is not null ? new[] { "--project", project } : Array.Empty<string>();
            await Serena.Mcp.Program.Main(mcpArgs);
        });

        return command;
    }

    private static Command CreateConfigCommand()
    {
        var command = new Command("config") { Description = "Show or edit configuration" };

        var showCommand = new Command("show") { Description = "Show current configuration" };
        showCommand.SetAction((ParseResult _) =>
        {
            string configPath = Path.Combine(SerenaPaths.Instance.SerenaUserHomeDir, "config.yml");
            Console.WriteLine(File.Exists(configPath)
                ? File.ReadAllText(configPath)
                : "No configuration file found. Run 'serena config init' to create one.");
        });

        var initCommand = new Command("init") { Description = "Initialize default configuration" };
        initCommand.SetAction((ParseResult _) =>
        {
            string configPath = Path.Combine(SerenaPaths.Instance.SerenaUserHomeDir, "config.yml");
            if (File.Exists(configPath))
            {
                Console.WriteLine($"Configuration already exists at: {configPath}");
            }
            else
            {
                SerenaPaths.Instance.EnsureDirectories();
                File.WriteAllText(configPath, """
                    # Serena Configuration
                    # Add your projects below:
                    projects: []
                    # default_project: my-project
                    """.Replace("                    ", ""));
                Console.WriteLine($"Created configuration at: {configPath}");
            }
        });

        command.Add(showCommand);
        command.Add(initCommand);
        return command;
    }

    private static Command CreateRegisterCommand()
    {
        var pathArg = new Argument<string>("path") { Description = "Path to the project directory" };
        var nameOption = new Option<string?>("--name") { Description = "Project name (defaults to directory name)" };
        var descOption = new Option<string?>("--description") { Description = "Project description" };

        var command = new Command("register") { Description = "Register a project in ~/.serena/config.yml" };
        command.Add(pathArg);
        command.Add(nameOption);
        command.Add(descOption);

        command.SetAction((ParseResult parseResult) =>
        {
            string rawPath = parseResult.GetValue(pathArg)!;
            string projectPath = Path.GetFullPath(rawPath);
            string? name = parseResult.GetValue(nameOption);
            string? description = parseResult.GetValue(descOption);

            if (!Directory.Exists(projectPath))
            {
                Console.Error.WriteLine($"Error: Directory not found: {projectPath}");
                return;
            }

            name ??= Path.GetFileName(projectPath);

            string configPath = Path.Combine(SerenaPaths.Instance.SerenaUserHomeDir, "config.yml");
            var configLogger = NullLogger<SerenaConfig>.Instance;

            var result = SerenaConfig.LoadFromFile(configPath, configLogger);
            var config = result.IsSuccess ? result.Value : new SerenaConfig(configLogger);

            config.RegisterProject(new RegisteredProject
            {
                Name = name,
                Path = projectPath,
                Description = description
            });

            // Save back to YAML
            var model = new
            {
                projects = config.RegisteredProjects.Values.Select(p => new
                {
                    name = p.Name,
                    path = p.Path,
                    description = p.Description
                }).ToArray(),
                default_project = config.DefaultProject
            };

            SerenaPaths.Instance.EnsureDirectories();
            YamlConfigLoader.Save(configPath, model);
            Console.WriteLine($"Registered project '{name}' at {projectPath}");
        });

        return command;
    }

    private static Command CreateListProjectsCommand()
    {
        var command = new Command("list-projects") { Description = "Show registered projects" };

        command.SetAction((ParseResult _) =>
        {
            string configPath = Path.Combine(SerenaPaths.Instance.SerenaUserHomeDir, "config.yml");
            var configLogger = NullLogger<SerenaConfig>.Instance;

            var result = SerenaConfig.LoadFromFile(configPath, configLogger);
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine($"Error loading config: {result.Error}");
                return;
            }

            var config = result.Value;
            if (config.RegisteredProjects.Count == 0)
            {
                Console.WriteLine("No projects registered. Use 'serena register <path>' to add one.");
                return;
            }

            Console.WriteLine("Registered Projects:");
            Console.WriteLine();
            foreach (var project in config.RegisteredProjects.Values)
            {
                string marker = project.Name == config.DefaultProject ? " (default)" : "";
                Console.WriteLine($"  {project.Name}{marker}");
                Console.WriteLine($"    Path: {project.Path}");
                if (project.Description is not null)
                {
                    Console.WriteLine($"    Description: {project.Description}");
                }
                Console.WriteLine();
            }
        });

        return command;
    }

    private static Command CreateSetupCommand()
    {
        var command = new Command("setup") { Description = "Initialize .serena/ directory in the current project" };

        command.SetAction((ParseResult _) =>
        {
            string projectRoot = Directory.GetCurrentDirectory();
            string serenaDir = Path.Combine(projectRoot, ".serena");

            if (Directory.Exists(serenaDir))
            {
                Console.WriteLine($".serena directory already exists at: {serenaDir}");
                return;
            }

            Directory.CreateDirectory(serenaDir);
            string projectYml = Path.Combine(serenaDir, "project.yml");
            string projectName = Path.GetFileName(projectRoot);

            File.WriteAllText(projectYml, $"""
                # Serena Project Configuration
                project_name: {projectName}
                encoding: utf-8
                """);

            Directory.CreateDirectory(Path.Combine(serenaDir, "memories"));

            Console.WriteLine($"Initialized .serena directory at: {serenaDir}");
            Console.WriteLine($"  Created: project.yml");
            Console.WriteLine($"  Created: memories/");
            Console.WriteLine();
            Console.WriteLine($"Next: Run 'serena register {projectRoot}' to register this project.");
        });

        return command;
    }

    private static Command CreateDoctorCommand()
    {
        var command = new Command("doctor") { Description = "Check language server prerequisites and availability" };

        command.SetAction((ParseResult _) => RunDoctor());

        return command;
    }

    private static void RunDoctor()
    {
        Console.WriteLine("Serena Doctor — checking language server prerequisites");
        Console.WriteLine();

        CheckPrerequisite("node", "--version", "Node.js", "https://nodejs.org");
        CheckPrerequisite("npm", "--version", "npm", "Bundled with Node.js");
        CheckPrerequisite("dotnet", "--version", ".NET SDK", "https://dot.net");
        Console.WriteLine();

        string lsBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".serena-dotnet", "language-servers");

        string dotnetToolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools");

        Console.WriteLine("Language Servers:");

        // C# — installed as a dotnet global tool
        CheckLanguageServer("C# (Roslyn)", "roslyn-language-server",
            localDir: null, dotnetToolsDir: dotnetToolsDir);

        // TypeScript — installed locally via npm
        CheckLanguageServer("TypeScript", "typescript-language-server",
            localDir: Path.Combine(lsBase, "typescript"), dotnetToolsDir: null);

        // Python — installed locally via npm
        CheckLanguageServer("Python (Pyright)", "pyright-langserver",
            localDir: Path.Combine(lsBase, "python"), dotnetToolsDir: null);

        // Rust — user-installed
        CheckLanguageServer("Rust (rust-analyzer)", "rust-analyzer",
            localDir: null, dotnetToolsDir: null);

        // Go — user-installed
        CheckLanguageServer("Go (gopls)", "gopls",
            localDir: null, dotnetToolsDir: null);

        Console.WriteLine();
        Console.WriteLine("Language servers are auto-installed when the MCP server starts.");
        Console.WriteLine("If any are missing, ensure prerequisites are installed and restart the server.");
    }

    private static void CheckLanguageServer(string displayName, string binaryName,
        string? localDir, string? dotnetToolsDir)
    {
        // Check local npm install
        if (localDir is not null)
        {
            string binDir = Path.Combine(localDir, "node_modules", ".bin");
            string[] extensions = PlatformUtils.IsWindows ? [".cmd", ".exe", ""] : [""];
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(binDir, binaryName + ext);
                if (File.Exists(candidate))
                {
                    Console.WriteLine($"  ✓ {displayName,-25} {candidate} (local)");
                    return;
                }
            }
        }

        // Check dotnet tools dir
        if (dotnetToolsDir is not null)
        {
            string[] extensions = PlatformUtils.IsWindows ? [".exe", ".cmd", ""] : [""];
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(dotnetToolsDir, binaryName + ext);
                if (File.Exists(candidate))
                {
                    Console.WriteLine($"  ✓ {displayName,-25} {candidate} (dotnet tool)");
                    return;
                }
            }
        }

        // Check global PATH
        string? globalPath = FindExecutable(binaryName);
        if (globalPath is not null)
        {
            Console.WriteLine($"  ✓ {displayName,-25} {globalPath} (global)");
            return;
        }

        Console.WriteLine($"  ✗ {displayName,-25} not found");
    }

    private static bool CheckPrerequisite(string binary, string versionArg, string displayName, string installHint)
    {
        try
        {
            string fileName = PlatformUtils.IsWindows && binary == "npm" ? "npm.cmd" : binary;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = versionArg,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                Console.WriteLine($"  ✗ {displayName,-25} not found — {installHint}");
                return false;
            }
            string version = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(TimeSpan.FromSeconds(10));
            Console.WriteLine($"  ✓ {displayName,-25} {version}");
            return true;
        }
        catch
        {
            Console.WriteLine($"  ✗ {displayName,-25} not found — {installHint}");
            return false;
        }
    }

    private static string? FindExecutable(string name)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] extensions = PlatformUtils.IsWindows ? [".exe", ".cmd", ".bat", ""] : [""];

        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static Command CreateVersionCommand()
    {
        var command = new Command("version") { Description = "Show version info" };

        command.SetAction((ParseResult _) =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version ?? new Version(0, 1, 0);
            Console.WriteLine($"Serena .NET v{version.Major}.{version.Minor}.{version.Build}");
            Console.WriteLine($"Runtime: {Environment.Version}");
            Console.WriteLine($"OS: {Environment.OSVersion}");
        });

        return command;
    }

    private static Command CreateProjectCommand()
    {
        var projectCommand = new Command("project") { Description = "Project management commands" };
        projectCommand.Add(CreateProjectIndexCommand());
        projectCommand.Add(CreateProjectIndexFileCommand());
        return projectCommand;
    }

    private static Command CreateProjectIndexCommand()
    {
        var pathArg = new Argument<string>("path")
        {
            Description = "Path to the project directory (default: current directory)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var logLevelOption = new Option<string>("--log-level")
        {
            Description = "Log level for indexing (Debug, Information, Warning, Error, Critical)",
            DefaultValueFactory = _ => "Warning"
        };

        var timeoutOption = new Option<double>("--timeout")
        {
            Description = "Timeout in seconds for indexing a single file",
            DefaultValueFactory = _ => 10.0
        };

        var command = new Command("index")
        {
            Description = "Index a project by requesting symbols for all source files"
        };
        command.Add(pathArg);
        command.Add(logLevelOption);
        command.Add(timeoutOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            string rawPath = parseResult.GetValue(pathArg) ?? ".";
            string projectRoot = Path.GetFullPath(rawPath);
            string logLevel = parseResult.GetValue(logLevelOption) ?? "Warning";
            double timeout = parseResult.GetValue(timeoutOption);

            if (!Directory.Exists(projectRoot))
            {
                Console.Error.WriteLine($"Error: Directory not found: {projectRoot}");
                return;
            }

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(Enum.Parse<LogLevel>(logLevel, ignoreCase: true));
            });

            var registry = new LanguageServerRegistry();
            BuiltInLanguageServers.RegisterAll(registry, loggerFactory);
            AdditionalLanguageServers.RegisterAll(registry, loggerFactory);
            var indexer = new ProjectIndexer(registry, loggerFactory);

            Console.WriteLine($"Indexing symbols in {projectRoot} …");

            var result = await indexer.IndexProjectAsync(
                projectRoot,
                onProgress: ReportIndexProgress,
                onStatus: status => { Console.WriteLine($"  {status}"); Console.Out.Flush(); },
                perFileTimeout: TimeSpan.FromSeconds(timeout),
                ct: ct);

            Console.WriteLine();
            Console.WriteLine();

            await ReportIndexResultAsync(result, projectRoot, ct);
        });

        return command;
    }

    private static void ReportIndexProgress(IndexProgress progress)
    {
        string status = progress.Success ? "✓" : "✗";
        int pct = (int)(100.0 * progress.CurrentFile / progress.TotalFiles);
        Console.Write($"\r  [{pct,3}%] {progress.CurrentFile}/{progress.TotalFiles} {status} {progress.FilePath}");
        if (!progress.Success)
        {
            Console.WriteLine();
            Console.Error.WriteLine($"    Error: {progress.Error}");
        }
    }

    private static async Task ReportIndexResultAsync(
        IndexResult result, string projectRoot, CancellationToken ct)
    {
        Console.WriteLine("Indexed files per language:");
        foreach (var (lang, count) in result.FilesPerLanguage)
        {
            Console.WriteLine($"  {lang.ToIdentifier()}: {count}");
        }

        if (result.SkippedFiles > 0)
        {
            Console.WriteLine($"  (skipped {result.SkippedFiles} files with no matching language server)");
        }

        if (result.Failures.Count > 0)
        {
            string logDir = Path.Combine(projectRoot, ".serena", "logs");
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "indexing.txt");

            var lines = result.Failures.SelectMany(f => new[] { f.FilePath, f.Error, "" });
            await File.WriteAllLinesAsync(logFile, lines, ct);

            Console.WriteLine();
            Console.WriteLine($"Failed to index {result.Failures.Count} files, see:");
            Console.WriteLine($"  {logFile}");
        }
    }

    private static Command CreateProjectIndexFileCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "Path to the source file to index" };
        var projectOption = new Option<string?>("--project") { Description = "Project root directory (default: current directory)" };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Print detailed symbol information" };

        var command = new Command("index-file")
        {
            Description = "Index a single file and show its symbols (useful for debugging)"
        };
        command.Add(fileArg);
        command.Add(projectOption);
        command.Add(verboseOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            string filePath = parseResult.GetValue(fileArg)!;
            string projectRoot = Path.GetFullPath(parseResult.GetValue(projectOption) ?? ".");
            bool verbose = parseResult.GetValue(verboseOption);

            string absoluteFile = Path.GetFullPath(filePath);
            if (!File.Exists(absoluteFile))
            {
                Console.Error.WriteLine($"Error: File not found: {absoluteFile}");
                return;
            }

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
            });

            var registry = new LanguageServerRegistry();
            BuiltInLanguageServers.RegisterAll(registry, loggerFactory);
            AdditionalLanguageServers.RegisterAll(registry, loggerFactory);
            var indexer = new ProjectIndexer(registry, loggerFactory);

            string ext = Path.GetExtension(absoluteFile);
            var language = LanguageExtensions.FromFileExtension(ext);
            Console.WriteLine($"Indexing file '{filePath}' (language: {language?.ToIdentifier() ?? "unknown"}) …");

            var result = await indexer.IndexFileAsync(projectRoot, absoluteFile, ct);

            if (result.IsFailure)
            {
                Console.Error.WriteLine($"Error: {result.Error}");
                return;
            }

            var symbols = result.Value;
            Console.WriteLine($"Successfully indexed file '{filePath}', {symbols.Count} top-level symbols found.");

            if (verbose)
            {
                PrintSymbols(symbols, indent: 0);
            }
        });

        return command;
    }

    private static void PrintSymbols(IReadOnlyList<Lsp.Client.UnifiedSymbolInformation> symbols, int indent)
    {
        string prefix = new(' ', indent * 2);
        foreach (var sym in symbols)
        {
            int startLine = sym.BodyRange?.Start?.Line ?? sym.SelectionRange?.Start?.Line ?? -1;
            Console.WriteLine($"{prefix}{sym.Kind} {sym.Name} (line {startLine + 1})");
            if (sym.Children.Count > 0)
            {
                PrintSymbols(sym.Children, indent + 1);
            }
        }
    }
}
