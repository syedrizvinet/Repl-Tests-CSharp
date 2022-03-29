﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.PackageManagement;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.PackageExtraction;
using NuGet.Packaging.Signing;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;
using PrettyPrompt.Consoles;

namespace CSharpRepl.Services.Nuget;

internal sealed class NugetPackageInstaller
{
    private static readonly Mutex MultipleNuspecPatchMutex = new(false, $"CSharpRepl_{nameof(MultipleNuspecPatchMutex)}");

    private readonly ConsoleNugetLogger logger;

    public NugetPackageInstaller(IConsole console, Configuration configuration)
    {
        this.logger = new ConsoleNugetLogger(console, configuration);
    }

    public async Task<ImmutableArray<PortableExecutableReference>> InstallAsync(
        string packageId,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        logger.Reset();

        try
        {
            ISettings settings = ReadSettings();
            var frameworkVersion = GetCurrentFramework();
            var nuGetProject = CreateFolderProject(Path.Combine(Configuration.ApplicationDirectory, "packages"));
            var sourceRepositoryProvider = new SourceRepositoryProvider(new PackageSourceProvider(settings), Repository.Provider.GetCoreV3());
            var packageManager = CreatePackageManager(settings, nuGetProject, sourceRepositoryProvider);

            using var sourceCacheContext = new SourceCacheContext();

            var resolutionContext = new ResolutionContext(
                DependencyBehavior.Lowest,
                includePrelease: true, includeUnlisted: true,
                VersionConstraints.None, new GatherCache(), sourceCacheContext
            );

            var primarySourceRepositories = sourceRepositoryProvider.GetRepositories();
            var packageIdentity = string.IsNullOrEmpty(version)
                ? await QueryLatestPackageVersion(packageId, nuGetProject, resolutionContext, primarySourceRepositories, cancellationToken)
                : new PackageIdentity(packageId, new NuGetVersion(version));

            if (!packageIdentity.HasVersion)
            {
                logger.LogFinish($"Could not find package '{packageIdentity}'", success: false);
                return ImmutableArray<PortableExecutableReference>.Empty;
            }

            var skipInstall = nuGetProject.PackageExists(packageIdentity);
            if (!skipInstall)
            {
                await DownloadPackageAsync(packageIdentity, packageManager, resolutionContext, primarySourceRepositories, settings, cancellationToken);
            }

            logger.LogInformationSummary($"Adding references for '{packageIdentity}'");
            var references = await GetAssemblyReferenceWithDependencies(frameworkVersion, nuGetProject, packageIdentity, cancellationToken);

            logger.LogFinish($"Package '{packageIdentity}' was successfully installed.", success: true);
            return references;
        }
        catch (Exception ex)
        {
            logger.LogFinish($"Could not find package '{packageId}'. Error: {ex}", success: false);
            return ImmutableArray<PortableExecutableReference>.Empty;
        }
    }

    private static async Task<ImmutableArray<PortableExecutableReference>> GetAssemblyReferenceWithDependencies(
        NuGetFramework frameworkVersion,
        FolderNuGetProject nuGetProject,
        PackageIdentity packageIdentity,
        CancellationToken cancellationToken)
    {
        var packages = await GetDependencies(frameworkVersion, nuGetProject, packageIdentity, cancellationToken);

        // get the filenames of everything under the "lib" directory, for both the provided nuget package and all its dependent packages.
        var packageContents = await Task.WhenAll(
            packages.Select(
                async package =>
                {
                    var libs = (await package.Value.GetLibItemsAsync(cancellationToken));
                    package.Value.Dispose();
                    return (PackageId: package.Key.ToString(), Libs: libs);
                })
        );

        var references = packageContents
            .SelectMany(contents => EnumerateReferences(contents.PackageId, contents.Libs))
            .ToImmutableArray();

        return references;

        IEnumerable<PortableExecutableReference> EnumerateReferences(string packageId, IEnumerable<FrameworkSpecificGroup> libs)
        {
            //we want to use the highest TargetFramework compatible with current frameworkVersion
            foreach (var lib in libs.OrderByDescending(l => l.TargetFramework.Version))
            {
                // filter down to only the dependencies that are compatible with the current framework.
                // e.g. netstandard2.1 packages are compatible with net5 applications.
                if (DefaultCompatibilityProvider.Instance.IsCompatible(frameworkVersion, lib.TargetFramework))
                {
                    foreach (var filePath in lib.Items.Where(filepath => Path.GetExtension(filepath) == ".dll"))
                    {
                        yield return MetadataReference.CreateFromFile(Path.Combine(nuGetProject.Root, packageId, filePath));
                    }

                    //after enumerating references of the highest compatible version, we can stop
                    break;
                }
            }
        }
    }

    private static async Task<Dictionary<PackageIdentity, PackageFolderReader>> GetDependencies(
        NuGetFramework frameworkVersion,
        FolderNuGetProject nuGetProject,
        PackageIdentity packageIdentity,
        CancellationToken cancellationToken)
    {
        var dependencies = new Dictionary<PackageIdentity, PackageFolderReader>();
        await GetDependencies(frameworkVersion, nuGetProject, packageIdentity, dependencies, cancellationToken);
        return dependencies;
    }

    private static async Task GetDependencies(
        NuGetFramework frameworkVersion,
        FolderNuGetProject nuGetProject,
        PackageIdentity packageIdentity,
        Dictionary<PackageIdentity, PackageFolderReader> aggregatedDependencies,
        CancellationToken cancellationToken)
    {
        var installedPath = new DirectoryInfo(Path.Combine(nuGetProject.Root, packageIdentity.ToString()));
        if (!installedPath.Exists)
            return;

        lock (aggregatedDependencies)
        {
            if (aggregatedDependencies.ContainsKey(packageIdentity))
                return;
        }

        var reader = new PackageFolderReader(installedPath);
        lock (aggregatedDependencies)
        {
            aggregatedDependencies[packageIdentity] = reader;
        }

        CheckAndFixMultipleNuspecFilesExistance(installedPath.FullName);
        var dependencyGroup = (await reader.GetPackageDependenciesAsync(cancellationToken)).ToArray();

        if (!dependencyGroup.Any())
            return;

        var firstLevelDependencies = dependencyGroup
            .Last(group => DefaultCompatibilityProvider.Instance.IsCompatible(frameworkVersion, group.TargetFramework))
            .Packages
            .Select(p => new PackageIdentity(p.Id, p.VersionRange.MinVersion));

        await Task.WhenAll(
            firstLevelDependencies.Select(p => GetDependencies(frameworkVersion, nuGetProject, p, aggregatedDependencies, cancellationToken))
        );
    }

    private static NuGetFramework GetCurrentFramework() =>
        NuGetFramework.Parse(
            Assembly
                .GetEntryAssembly()?
                .GetCustomAttribute<TargetFrameworkAttribute>()?
                .FrameworkName
        );

    private async Task DownloadPackageAsync(
        PackageIdentity packageIdentity,
        NuGetPackageManager packageManager,
        ResolutionContext resolutionContext,
        IEnumerable<SourceRepository> primarySourceRepositories,
        ISettings settings,
        CancellationToken cancellationToken)
    {
        var clientPolicyContext = ClientPolicyContext.GetClientPolicy(settings, logger);
        var projectContext = new ConsoleProjectContext(logger)
        {
            PackageExtractionContext = new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                PackageExtractionBehavior.XmlDocFileSaveMode,
                clientPolicyContext,
                logger)
            {
                CopySatelliteFiles = false
            }
        };

        await packageManager.InstallPackageAsync(packageManager.PackagesFolderNuGetProject,
            packageIdentity, resolutionContext, projectContext,
            primarySourceRepositories, Array.Empty<SourceRepository>(), cancellationToken);
    }

    private async Task<PackageIdentity> QueryLatestPackageVersion(
        string packageId,
        FolderNuGetProject nuGetProject,
        ResolutionContext resolutionContext,
        IEnumerable<SourceRepository> primarySourceRepositories,
        CancellationToken cancellationToken)
    {
        var resolvePackage = await NuGetPackageManager.GetLatestVersionAsync(
            packageId, nuGetProject,
            resolutionContext, primarySourceRepositories,
            logger, cancellationToken
        );
        return new PackageIdentity(packageId, resolvePackage.LatestVersion);
    }

    private static NuGetPackageManager CreatePackageManager(
        ISettings settings,
        FolderNuGetProject nuGetProject,
        SourceRepositoryProvider sourceRepositoryProvider)
    {
        var packageManager = new NuGetPackageManager(sourceRepositoryProvider, settings, nuGetProject.Root)
        {
            PackagesFolderNuGetProject = nuGetProject
        };
        return packageManager;
    }

    private static FolderNuGetProject CreateFolderProject(string directory)
    {
        string projectRoot = Path.GetFullPath(directory);
        Directory.CreateDirectory(projectRoot);
        if (!Directory.Exists(projectRoot)) Directory.CreateDirectory(projectRoot);
        var nuGetProject = new FolderNuGetProject(
            projectRoot,
            packagePathResolver: new PackagePathResolver(projectRoot)
        );
        return nuGetProject;
    }

    private static ISettings ReadSettings()
    {
        var curDir = Directory.GetCurrentDirectory();
        ISettings settings = File.Exists(Path.Combine(curDir, Settings.DefaultSettingsFileName))
            ? Settings.LoadSpecificSettings(curDir, Settings.DefaultSettingsFileName)
            : Settings.LoadDefaultSettings(curDir);
        return settings;
    }

    /// <summary>
    /// This is a patch for https://github.com/waf/CSharpRepl/issues/52.
    /// The problem emerges on systems with case-sensitive file system.
    /// There can be multiple nuspec files differing only in name casing in the package folder.lder.
    /// Not sure why this happens (I suspect there is a bug in NuGet.PackageManagement).
    /// </summary>
    private static void CheckAndFixMultipleNuspecFilesExistance(string packageDirectoryPath)
    {
        MultipleNuspecPatchMutex.WaitOne();
        try
        {
            var nuspecFileGroups = Directory.EnumerateFiles(packageDirectoryPath)
                .Where(f => f.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (var nuspecsWithSameName in nuspecFileGroups)
            {
                foreach (var duplicateNuspec in nuspecsWithSameName.Skip(1))
                {
                    File.Delete(duplicateNuspec);
                }
            }
        }
        finally
        {
            MultipleNuspecPatchMutex.ReleaseMutex();
        }
    }
}