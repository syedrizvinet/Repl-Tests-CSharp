﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using PrettyPrompt.Consoles;
using CSharpRepl.Services.Roslyn.MetadataResolvers;
using CSharpRepl.Services.Roslyn.References;

namespace CSharpRepl.Services.Roslyn.Scripting
{
    /// <summary>
    /// Uses the Roslyn Scripting APIs to execute C# code in a string.
    /// </summary>
    internal sealed class ScriptRunner
    {
        private readonly IConsole console;
        private readonly InteractiveAssemblyLoader assemblyLoader;
        private readonly NugetPackageMetadataResolver nugetResolver;
        private readonly AssemblyReferenceService referenceAssemblyService;
        private ScriptOptions scriptOptions;
        private ScriptState<object>? state;

        public ScriptRunner(IConsole console, CSharpCompilationOptions compilationOptions, AssemblyReferenceService referenceAssemblyService)
        {
            this.console = console;
            this.referenceAssemblyService = referenceAssemblyService;
            this.assemblyLoader = new InteractiveAssemblyLoader(new MetadataShadowCopyProvider());
            this.nugetResolver = new NugetPackageMetadataResolver(console);

            this.scriptOptions = ScriptOptions.Default
                .WithMetadataResolver(new CompositeMetadataReferenceResolver(
                    nugetResolver,
                    new ProjectFileMetadataResolver(console),
                    new AssemblyReferenceMetadataResolver(console, referenceAssemblyService)
                ))
                .WithReferences(referenceAssemblyService.LoadedImplementationAssemblies)
                .WithAllowUnsafe(compilationOptions.AllowUnsafe)
                .AddImports(compilationOptions.Usings);
        }

        /// <summary>
        /// Accepts a string containing C# code and runs it. Subsequent invocations will use the state from earlier invocations.
        /// </summary>
        public async Task<EvaluationResult> RunCompilation(string text, string[]? args = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var nugetCommands = text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(nugetResolver.IsNugetReference);
                foreach (var nugetCommand in nugetCommands)
                {
                    var assemblyReferences = await nugetResolver.InstallNugetPackageAsync(nugetCommand, cancellationToken).ConfigureAwait(false);
                    this.scriptOptions = this.scriptOptions.AddReferences(assemblyReferences);
                }

                state = await EvaluateStringWithStateAsync(text, state, assemblyLoader, this.scriptOptions, args, cancellationToken).ConfigureAwait(false);

                return state.Exception is null
                    ? CreateSuccessfulResult(text, state)
                    : new EvaluationResult.Error(this.state.Exception);
            }
            catch (Exception oce) when (oce is OperationCanceledException || oce.InnerException is OperationCanceledException)
            {
                // user can cancel by pressing ctrl+c, which triggers the CancellationToken
                return new EvaluationResult.Cancelled();
            }
            catch (Exception exception)
            {
                return new EvaluationResult.Error(exception);
            }
        }

        private EvaluationResult.Success CreateSuccessfulResult(string text, ScriptState<object> state)
        {
            referenceAssemblyService.AddImplementationAssemblyReferences(state.Script.GetCompilation().References);
            var frameworkReferenceAssemblies = referenceAssemblyService.LoadedReferenceAssemblies;
            var frameworkImplementationAssemblies = referenceAssemblyService.LoadedImplementationAssemblies;
            this.scriptOptions = this.scriptOptions.WithReferences(frameworkImplementationAssemblies);
            return new EvaluationResult.Success(text, state.ReturnValue, frameworkImplementationAssemblies.Concat(frameworkReferenceAssemblies).ToList());
        }

        private Task<ScriptState<object>> EvaluateStringWithStateAsync(string text, ScriptState<object>? state, InteractiveAssemblyLoader assemblyLoader, ScriptOptions scriptOptions, string[]? args = null, CancellationToken cancellationToken = default)
        {
            return state is null
                ? CSharpScript
                    .Create(text, scriptOptions, globalsType: typeof(ScriptGlobals), assemblyLoader: assemblyLoader)
                    .RunAsync(globals: CreateGlobalsObject(args), cancellationToken: cancellationToken)
                : state
                    .ContinueWithAsync(text, scriptOptions, cancellationToken: cancellationToken);
        }

        private ScriptGlobals CreateGlobalsObject(string[]? args)
        {
            return new ScriptGlobals(console, args ?? Array.Empty<string>());
        }
    }
}
