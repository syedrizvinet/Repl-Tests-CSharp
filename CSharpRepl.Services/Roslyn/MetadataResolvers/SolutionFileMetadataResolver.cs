﻿using CSharpRepl.Services.Dotnet;
using Microsoft.CodeAnalysis;
using PrettyPrompt.Consoles;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpRepl.Services.Roslyn.MetadataResolvers;


internal sealed class SolutionFileMetadataResolver : AlternativeReferenceResolver
{
    private readonly IDotnetBuilder builder;
    private readonly IConsole console;

    public SolutionFileMetadataResolver(IDotnetBuilder builder, IConsole console)
    {
        this.builder = builder;
        this.console = console;
    }

    public override bool CanResolve(string reference)
    {
        return reference.Replace("\"", string.Empty).EndsWith(".sln");
    }

    public override async Task<ImmutableArray<PortableExecutableReference>> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var solutionPath = Path.GetFullPath(reference
            .Split('\"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Last());

        var (exitCode, output) = await builder.BuildAsync(solutionPath, cancellationToken);

        var projectPaths = builder
            .ParseBuildGraph(output)
            .Select(kvp => Path.GetFullPath(kvp.Value)); //GetFullPath will normalize separators

        if (!projectPaths.Any())
        {
            console.WriteErrorLine("Project reference not added: could not determine built assembly");
            return ImmutableArray<PortableExecutableReference>.Empty;
        }

        return projectPaths
                .Select(projectPath => MetadataReference.CreateFromFile(projectPath))
                .ToImmutableArray();
    }
}