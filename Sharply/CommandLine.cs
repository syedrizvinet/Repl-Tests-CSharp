﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using static System.Environment;

namespace Sharply
{
    /// <summary>
    /// Parses command line arguments.
    /// </summary>
    /// <remarks>
    /// Doing our own parsing, instead of using an existing library, is questionable. However, I wasn't able to find an
    /// existing library that covers the following:
    ///     - Supports response files (i.e. ".rsp" files) for compatibility with other interactive C# consoles (e.g. csi).
    ///     - Supports windows-style forward slash arguments (e.g. /u), again for compatibility with other C# consoles.
    /// Additionally, the parsing logic is only ~50 lines of code with no reflection, so maybe it's not too big a sin!
    /// </remarks>
    class CommandLine
    {
        public static Configuration ParseArguments(string[] args, Configuration existingConfiguration = null)
        {
            string currentSwitch = "";
            return args
                .SelectMany(arg => arg.Split(new[] { ":", "=" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Aggregate(existingConfiguration ?? new Configuration(), (config, arg) =>
                {
                    //
                    // process option flags
                    //
                    if (arg == "-v" || arg == "--version" || arg == "/v")
                        config.ShowVersion = true;
                    else if (arg == "-h" || arg == "--help" || arg == "/h" || arg == "/?" || arg == "-?")
                        config.ShowHelp = true;
                    //
                    // process option names
                    //
                    else if (arg == "-r" || arg == "--reference" || arg == "/r")
                        currentSwitch = "-r";
                    else if (arg == "-u" || arg == "--using"|| arg == "/u")
                        currentSwitch = "-u";
                    else if (arg == "-t" || arg == "--theme"|| arg == "/t")
                        currentSwitch = "-t";
                    //
                    // process option values
                    //
                    else if (currentSwitch == "-r")
                        config.References.Add(arg);
                    else if (currentSwitch == "-u")
                        config.Usings.Add(arg);
                    else if (currentSwitch == "-t")
                        config.Theme = arg;
                    // 
                    // Process positional parameters
                    // 
                    else if (arg.EndsWith(".csx"))
                    {
                        if (!File.Exists(arg)) throw new FileNotFoundException(arg);
                        config.Load = arg;
                    }
                    else if (arg.EndsWith(".rsp"))
                    {
                        string path = arg.TrimStart('@'); // a common convention is to prefix rsp files with '@'
                        if (!File.Exists(path)) throw new FileNotFoundException(path);
                        if (existingConfiguration is not null) throw new InvalidOperationException("Response files cannot be nested.");
                        var responseFile = File
                            .ReadAllText(path)
                            .Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.None);
                        config = ParseArguments(responseFile, config);
                    }
                    else
                        throw new InvalidOperationException("Unknown command line option: " + arg);
                    return config;
                });
        }

        public static string GetHelp() =>
            GetVersion() + NewLine +
            "Usage: sharply [OPTIONS] [response-file.rsp] [script-file.csx]" + NewLine + NewLine +
            "Starts a REPL (read eval print loop) according to the provided command line [OPTIONS]." + NewLine +
            "These [OPTIONS] can be provided at the command line, or via a [response-file.rsp]." + NewLine +
            "A [script-file.csx], if provided, will be executed before the prompt starts." + NewLine + NewLine +
            "OPTIONS:" + NewLine +
            "  -r <dll> or --reference <dll>:            Add an assembly reference. May be specified multiple times." + NewLine +
            "  -u <namespace> or --using <namespace>:    Add a using statement. May be specified multiple times." + NewLine +
            "  -t <theme.json> or --theme <theme.json>:  Specify the theme file for syntax highlighting." + NewLine +
            "  -v or --version:                          Show version number and exit." + NewLine +
            "  -h or --help:                             Show this help and exit." + NewLine + NewLine +
            "response-file.rsp:" + NewLine +
            "  A file, with extension .rsp, containing the above command line [OPTIONS], one option per line." + NewLine + NewLine +
            "script-file.csx:" + NewLine +
            "  A file, with extension .csx, containing lines of C# to evaluate before starting the REPL." + NewLine;

        public static string GetVersion()
        {
            var product = nameof(Sharply);
            var version = Assembly
                .GetEntryAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                .InformationalVersion;
            return product + " " + version;
        }
    }

    class Configuration
    {
        public List<string> References { get; } = new();
        public List<string> Usings { get; } = new();
        public string Theme { get; set; }
        public string ResponseFile { get; set; }
        public string Load { get; set; }
        public bool ShowVersion { get; set; }
        public bool ShowHelp { get; set; }
    }
}
