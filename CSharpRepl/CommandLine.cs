﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using CSharpRepl.Services;
using CSharpRepl.Services.Roslyn;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using static System.Environment;

namespace CSharpRepl
{
    /// <summary>
    /// Parses command line arguments.
    /// </summary>
    /// <remarks>
    /// Doing our own parsing, instead of using an existing library, is questionable. However, I wasn't able to find an
    /// existing library that covers the following:
    ///     - Supports response files (i.e. ".rsp" files) for compatibility with other interactive C# consoles (e.g. csi).
    ///     - Supports windows-style forward slash arguments (e.g. /u), again for compatibility with other C# consoles.
    /// </remarks>
    internal static class CommandLine
    {
        public static Configuration ParseArguments(string[] args, Configuration existingConfiguration = null)
        {
            string currentSwitch = "";
            List<string> loadScriptArgs = null;
            var config = args
                .Aggregate(existingConfiguration ?? new Configuration(), (config, arg) =>
                {
                    // 
                    // Process positional parameters
                    // 
                    if(arg == "--")
                    {
                        loadScriptArgs = new List<string>();
                    }
                    else if (loadScriptArgs is not null)
                    {
                        loadScriptArgs.Add(arg);
                    }
                    else if (arg.EndsWith(".csx"))
                    {
                        if (!File.Exists(arg)) throw new FileNotFoundException($@"Script file ""{arg}"" was not found");
                        config.LoadScript = File.ReadAllText(arg);
                    }
                    else if (arg.EndsWith(".rsp"))
                    {
                        string path = arg.TrimStart('@'); // a common convention is to prefix rsp files with '@'
                        if (!File.Exists(path)) throw new FileNotFoundException($@"RSP file ""{path}"" was not found");
                        if (existingConfiguration is not null) throw new InvalidOperationException("Response files cannot be nested.");
                        var responseFile = File
                            .ReadAllLines(path)
                            .SelectMany(line =>
                                new string(line.TakeWhile(ch => ch != '#').ToArray()) // ignore comments
                                .Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.None)
                            )
                            .ToArray();
                        config = ParseArguments(responseFile, config);
                    }
                    //
                    // process option flags
                    //
                    else if (arg == "-v" || arg == "--version" || arg == "/v")
                        config.ShowVersionAndExit = true;
                    else if (arg == "-h" || arg == "--help" || arg == "/h" || arg == "/?" || arg == "-?")
                        config.ShowHelpAndExit = true;
                    //
                    // process option names, and optional values if they're provided like /r:Reference
                    //
                    else if (arg.StartsWith("-r") || arg.StartsWith("--reference") || arg.StartsWith("/r"))
                    {
                        currentSwitch = "--reference";
                        if(TryGetConcatenatedValue(arg, out string reference))
                        {
                            config.References.Add(reference);
                        }
                    }
                    else if (arg.StartsWith("-u") || arg.StartsWith("--using") || arg.StartsWith("/u"))
                    { 
                        currentSwitch = "--using";
                        if(TryGetConcatenatedValue(arg, out string usingNamespace))
                        {
                            config.Usings.Add(usingNamespace);
                        }
                    }
                    else if (arg.StartsWith("-f") || arg.StartsWith("--framework") || arg.StartsWith("/f"))
                    { 
                        currentSwitch = "--framework";
                        if(TryGetConcatenatedValue(arg, out string framework))
                        {
                             config.Framework = framework;
                        }
                    }
                    else if (arg.StartsWith("-t") || arg.StartsWith("--theme") || arg.StartsWith("/t"))
                    { 
                        currentSwitch = "--theme";
                        if(TryGetConcatenatedValue(arg, out string theme))
                        {
                             config.Theme = theme;
                        }
                    }
                    //
                    // process option values
                    //
                    else if (currentSwitch == "--reference")
                        config.References.Add(arg);
                    else if (currentSwitch == "--using")
                        config.Usings.Add(arg);
                    else if (currentSwitch == "--framework")
                        config.Framework = arg;
                    else if (currentSwitch == "--theme")
                        config.Theme = arg;
                    else
                        throw new InvalidOperationException("Unknown command line option: " + arg);
                    return config;
                });

            if(loadScriptArgs is not null)
            {
                config.LoadScriptArgs = loadScriptArgs.ToArray();
            }

            if (!SharedFramework.SupportedFrameworks.Contains(config.Framework.Split('/').First())) // allow trailing version numbers in framework name
            {
                throw new ArgumentException("Unknown Framework: " + config.Framework + ". Expected one of " + string.Join(", ", SharedFramework.SupportedFrameworks));
            }

            return config;
        }

        /// <summary>
        /// Parse value in a string like "/u:foo"
        /// </summary>
        private static bool TryGetConcatenatedValue(string arg, out string value)
        {
            var referenceValue = arg.Split(new[] { ':', '=' }, 2);
            if (referenceValue.Length == 2)
            {
                value = referenceValue[1];
                return true;
            }
            value = null;
            return false;
        }

        public static string GetHelp() =>
            GetVersion() + NewLine +
            "Usage: csharprepl [OPTIONS] [response-file.rsp] [script-file.csx] [-- <additional-arguments>]" + NewLine + NewLine +
            "Starts a REPL (read eval print loop) according to the provided [OPTIONS]." + NewLine +
            "These [OPTIONS] can be provided at the command line, or via a [response-file.rsp]." + NewLine +
            "A [script-file.csx], if provided, will be executed before the prompt starts." + NewLine + NewLine +
            "OPTIONS:" + NewLine +
            "  -r <dll> or --reference <dll>:             Reference an assembly or csproj file. May be specified multiple times." + NewLine +
            "  -u <namespace> or --using <namespace>:     Add a using statement. May be specified multiple times." + NewLine +
            "  -f <framework> or --framework <framework>: Reference a shared framework." + NewLine +
            "                                             Available shared frameworks: " + NewLine + GetInstalledFrameworks(
            "                                             ") + NewLine +
            "  -t <theme.json> or --theme <theme.json>:   Read a theme file for syntax highlighting. Respects the NO_COLOR standard." + NewLine +
            "  -v or --version:                           Show version number and exit." + NewLine +
            "  -h or --help:                              Show this help and exit." + NewLine + NewLine +
            "response-file.rsp:" + NewLine +
            "  A file, with extension .rsp, containing the above command line [OPTIONS], one option per line." + NewLine + NewLine +
            "script-file.csx:" + NewLine +
            "  A file, with extension .csx, containing lines of C# to evaluate before starting the REPL." + NewLine +
            "  Arguments to this script can be passed as <additional-arguments> and will be available in a global `args` variable." + NewLine;

        public static string GetVersion()
        {
            var product = "C# REPL";
            var version = Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                .InformationalVersion;
            return product + " " + version;
        }

        private static string GetInstalledFrameworks(string leftPadding)
        {
            var frameworkList = SharedFramework
                .SupportedFrameworks
                .Select(fx => leftPadding + "- " + fx + (fx == Configuration.FrameworkDefault ? " (default)" : ""));
            return string.Join(NewLine, frameworkList);
        }
    }
}
