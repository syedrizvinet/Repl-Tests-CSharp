﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using CSharpRepl.Services.Theming;

namespace CSharpRepl.Services.Roslyn.Formatting.CustomObjectFormatters;

internal sealed class KeyValuePairFormatter : CustomObjectFormatter
{
    public static readonly KeyValuePairFormatter Instance = new();

    public override Type Type => typeof(KeyValuePair<,>);

    private KeyValuePairFormatter() { }

    public override bool IsApplicable(object value)
        => value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == Type;

    public override StyledString FormatToText(object value, Level level, Formatter formatter)
    {
        var sb = new StyledStringBuilder();

        dynamic kv = value;
        if (level == Level.FirstDetailed)
        {
            // KeyValuePair<T1, T2> { key, value }
            sb.Append(formatter.FormatTypeName(value.GetType(), showNamespaces: false, useLanguageKeywords: true, hideSystemNamespace: true));
            sb.Append(" { ");
            sb.Append(formatter.FormatObjectToText(kv.Key, level));
            sb.Append(", ");
            sb.Append(formatter.FormatObjectToText(kv.Value, level));
        }
        else if (level == Level.FirstSimple)
        {
            // { Key: key, Value: value }
            sb.Append("{ Key: ");
            sb.Append(formatter.FormatObjectToText(kv.Key, level));
            sb.Append(", Value: ");
            sb.Append(formatter.FormatObjectToText(kv.Value, level));
        }
        else
        {
            // { key, value }
            sb.Append("{ ");
            sb.Append(formatter.FormatObjectToText(kv.Key, level));
            sb.Append(", ");
            sb.Append(formatter.FormatObjectToText(kv.Value, level));
        }
        sb.Append(" }");

        return sb.ToStyledString();
    }
}