﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Generic;

namespace CSharpRepl.Services
{
    /// <summary>
    /// Configuration from command line parameters
    /// </summary>
    public class Configuration
    {
        public HashSet<string> References { get; } = new();
        public HashSet<string> Usings { get; } = new();

        public const string FrameworkDefault = Roslyn.SharedFramework.NetCoreApp;
        public string Framework { get; set; } = FrameworkDefault;

        public string Theme { get; set; }
        public string ResponseFile { get; set; }
        public string LoadScript { get; set; }

        public bool ShowVersionAndExit { get; set; }
        public bool ShowHelpAndExit { get; set; }
    }
}
