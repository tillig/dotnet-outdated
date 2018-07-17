﻿using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DotNetOutdated.Exceptions;
using DotNetOutdated.Services;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Versioning;

[assembly: InternalsVisibleTo("DotNetOutdated.Tests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace DotNetOutdated
{
    [Command(
        Name = "dotnet outdated",
        FullName = "A .NET Core global tool to list outdated Nuget packages.")]
    [VersionOptionFromMember(MemberName = nameof(GetVersion))]
    class Program : CommandBase
    {
        private readonly IFileSystem _fileSystem;
        private readonly IReporter _reporter;
        private readonly INuGetPackageResolutionService _nugetService;
        private readonly IProjectAnalysisService _projectAnalysisService;
        private readonly IProjectDiscoveryService _projectDiscoveryService;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether to include auto-referenced packages.",
            LongName = "include-auto-references")]
        public bool IncludeAutoReferences { get; set; } = false;

        [Argument(0, Description = "The path to a .sln or .csproj file, or to a directory containing a .NET Core solution/project. " +
                                   "If none is specified, the current directory will be used.")]
        public string Path { get; set; }

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether to look for pre-release versions of packages. " +
                                                             "Possible values: Auto (default), Always or Never.",
            ShortName = "pr", LongName = "pre-release")]
        public PrereleaseReporting Prerelease { get; set; } = PrereleaseReporting.Auto;

        [Option(CommandOptionType.SingleValue, Description = "Specifies whether the package should be locked to the current Major or Minor version. " +
                                                             "Possible values: None (default), Major or Minor.",
            ShortName = "vl", LongName = "version-lock")]
        public VersionLock VersionLock { get; set; } = VersionLock.None;

        [Option(CommandOptionType.NoValue, Description = "Specifies whether it should detect transitive dependencies.",
            ShortName = "t", LongName = "transitive")]
        public bool Transitive { get; set; } = false;

        [Option(CommandOptionType.SingleValue, Description = "Defines how many levels deep transitive dependencies should be analyzed. " +
                                                             "Integer value (default = 1)",
            ShortName="td", LongName = "transitive-depth")]
        public int TransitiveDepth { get; set; } = 1;

        public static int Main(string[] args)
        {
            using (var services = new ServiceCollection()
                    .AddSingleton<IConsole, PhysicalConsole>()
                    .AddSingleton<IReporter>(provider => new ConsoleReporter(provider.GetService<IConsole>()))
                    .AddSingleton<IFileSystem, FileSystem>()
                    .AddSingleton<IProjectDiscoveryService, ProjectDiscoveryService>()
                    .AddSingleton<IProjectAnalysisService, ProjectAnalysisService>()
                    .AddSingleton<IDotNetRunner, DotNetRunner>()
                    .AddSingleton<IDependencyGraphService, DependencyGraphService>()
                    .AddSingleton<IDotNetRestoreService, DotNetRestoreService>()
                    .AddSingleton<INuGetPackageInfoService, NuGetPackageInfoService>()
                    .AddSingleton<INuGetPackageResolutionService, NuGetPackageResolutionService>()
                    .BuildServiceProvider())
            {
                var app = new CommandLineApplication<Program>
                {
                    ThrowOnUnexpectedArgument = false
                };
                app.Conventions
                    .UseDefaultConventions()
                    .UseConstructorInjection(services);

                return app.Execute(args);
            }
        }

        public static string GetVersion() => typeof(Program)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            .InformationalVersion;

        public Program(IFileSystem fileSystem, IReporter reporter, INuGetPackageResolutionService nugetService, IProjectAnalysisService projectAnalysisService,
            IProjectDiscoveryService projectDiscoveryService)
        {
            _fileSystem = fileSystem;
            _reporter = reporter;
            _nugetService = nugetService;
            _projectAnalysisService = projectAnalysisService;
            _projectDiscoveryService = projectDiscoveryService;
        }

        public async Task<int> OnExecute(CommandLineApplication app, IConsole console)
        {
            try
            {
                // If no path is set, use the current directory
                if (string.IsNullOrEmpty(Path))
                    Path = _fileSystem.Directory.GetCurrentDirectory();

                // Get all the projects
                console.Write("Discovering projects...");
                
                string projectPath = _projectDiscoveryService.DiscoverProject(Path);

                if (!console.IsOutputRedirected)
                    ClearCurrentConsoleLine();
                else
                    console.WriteLine();

                // Analyze the projects
                console.Write("Analyzing project and restoring packages...");
                
                var projects = _projectAnalysisService.AnalyzeProject(projectPath, Transitive, TransitiveDepth);

                if (!console.IsOutputRedirected)
                    ClearCurrentConsoleLine();
                else
                    console.WriteLine();

                // Analyze the dependencies
                await AnalyzeDependencies(projects, console);

                // Report on the outdated dependencies
                ReportOutdatedDependencies(projects, console);
                
                return 0;
            }
            catch (CommandValidationException e)
            {
                _reporter.Error(e.Message);

                return 1;
            }
        }

        private void PrintColorLegend(IConsole console)
        {
            console.WriteLine("Version color legend:");
            
            console.Write("<red>".PadRight(8), Constants.ReporingColors.MajorVersionUpgrade);
            console.WriteLine(": Major version update or pre-release version. Possible breaking changes.");
            console.Write("<yellow>".PadRight(8), Constants.ReporingColors.MinorVersionUpgrade);
            console.WriteLine(": Minor version update. Backwards-compatible features added.");
            console.Write("<green>".PadRight(8), Constants.ReporingColors.PatchVersionUpgrade);
            console.WriteLine(": Patch version update. Backwards-compatible bug fixes.");
        }
        
        private void ReportOutdatedDependencies(List<Project> projects, IConsole console)
        {
            foreach (var project in projects)
            {
                WriteProjectName(console, project);

                // Process each target framework with its related dependencies
                foreach (var targetFramework in project.TargetFrameworks)
                {
                    WriteTargetFramework(console, targetFramework);

                    var dependencies = targetFramework.Dependencies
                        .Where(d => d.LatestVersion > d.ResolvedVersion)
                        .ToList();

                    if (dependencies.Count > 0)
                    {
                        int[] columnWidths = dependencies.DetermineColumnWidths();

                        foreach (var dependency in dependencies)
                        {
                            string resolvedVersion = dependency.ResolvedVersion?.ToString() ?? "";
                            string latestVersion = dependency.LatestVersion?.ToString() ?? "";

                            console.WriteIndent();
                            console.Write(dependency.Description?.PadRight(columnWidths[0] + 2));
                            console.Write(resolvedVersion.PadRight(columnWidths[1]));
                            console.Write(" -> ");
                            console.Write(latestVersion.PadRight(columnWidths[2]), GetLatestVersionColor(dependency.LatestVersion, dependency.ResolvedVersion));

                            console.WriteLine();
                        }
                    }
                    else
                    {
                        console.WriteIndent();
                        console.WriteLine("-- No outdated dependencies --");
                    }
                }

                console.WriteLine();
            }

            PrintColorLegend(console);
        }

        private async Task AnalyzeDependencies(List<Project> projects, IConsole console)
        {
            if (console.IsOutputRedirected)
                console.WriteLine("Analyzing dependencies...");
                
            foreach (var project in projects)
            {
                // Process each target framework with its related dependencies
                foreach (var targetFramework in project.TargetFrameworks)
                {
                    var dependencies = targetFramework.Dependencies
                        .Where(d => IncludeAutoReferences || d.IsAutoReferenced == false)
                        .OrderBy(dependency => dependency.IsTransitive)
                        .ThenBy(dependency => dependency.Name)
                        .ToList();

                    for (var index = 0; index < dependencies.Count; index++)
                    {
                        var dependency = dependencies[index];
                        if (!console.IsOutputRedirected)
                            console.Write($"Analyzing dependencies for {project.Name} [{targetFramework.Name}] ({index + 1}/{dependencies.Count})");

                        var referencedVersion = dependency.ResolvedVersion;

                        dependency.LatestVersion = await _nugetService.ResolvePackageVersions(dependency.Name, referencedVersion, project.Sources, dependency.VersionRange,
                            VersionLock, Prerelease, targetFramework.Name, project.FilePath);

                        if (!console.IsOutputRedirected)
                            ClearCurrentConsoleLine();
                    }
                }
            }
        }

        private ConsoleColor GetLatestVersionColor(NuGetVersion latestVersion, NuGetVersion resolvedVersion)
        {
            if (latestVersion == null || resolvedVersion == null)
                return Console.ForegroundColor;

            if (latestVersion.Major > resolvedVersion.Major || resolvedVersion.IsPrerelease)
                return Constants.ReporingColors.MajorVersionUpgrade;
            if (latestVersion.Minor > resolvedVersion.Minor)
                return Constants.ReporingColors.MinorVersionUpgrade;
            if (latestVersion.Patch > resolvedVersion.Patch || latestVersion.Revision > resolvedVersion.Revision)
                return Constants.ReporingColors.PatchVersionUpgrade;

            return Console.ForegroundColor;
        }

        private static void WriteProjectName(IConsole console, Project project)
        {
            console.Write($"» {project.Name}", ConsoleColor.Yellow);
            console.WriteLine();
        }

        private static void WriteTargetFramework(IConsole console, Project.TargetFramework targetFramework)
        {
            console.WriteIndent();
            console.Write($"[{targetFramework.Name}]", ConsoleColor.Cyan);
            console.WriteLine();
        }
        
        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.BufferWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }
}
