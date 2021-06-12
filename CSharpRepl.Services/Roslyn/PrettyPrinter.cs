﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Microsoft.CodeAnalysis.CSharp.Scripting.Hosting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using System;
using System.Text.RegularExpressions;

namespace CSharpRepl.Services.Roslyn
{
    class PrettyPrinter
    {
        private readonly ObjectFormatter formatter;
        private readonly PrintOptions summaryOptions;
        private readonly PrintOptions detailedOptions;

        public PrettyPrinter()
        {
            this.formatter = CSharpObjectFormatter.Instance;
            this.summaryOptions = new PrintOptions
            {
                MemberDisplayFormat = MemberDisplayFormat.SingleLine,
                MaximumOutputLength = 20_000,
            };
            this.detailedOptions = new PrintOptions
            {
                MemberDisplayFormat = MemberDisplayFormat.SeparateLines,
                MaximumOutputLength = 20_000,
            };
        }

        public string FormatObject(object obj, bool displayDetails) => obj switch
        {
            null => null, // intercept null, don't print the string "null"
            string str when displayDetails => str, // when displayDetails is true, don't show the escaped string (i.e. interpret the escape characters, via displaying to console)
            _ => formatter.FormatObject(obj, displayDetails ? detailedOptions : summaryOptions)
        };

        public string FormatException(Exception obj, bool displayDetails) => displayDetails
            ? formatter.FormatException(obj)
            : obj.Message;
    }
}
