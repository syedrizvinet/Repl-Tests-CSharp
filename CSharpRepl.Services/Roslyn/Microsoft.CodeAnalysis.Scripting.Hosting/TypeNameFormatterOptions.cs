// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.Scripting.Hosting;

internal readonly struct TypeNameFormatterOptions
{
    public readonly int ArrayBoundRadix;
    public readonly bool ShowNamespaces;
    public readonly bool UseLanguageKeywords;
    public readonly bool HideSystemNamespace;

    public TypeNameFormatterOptions(int arrayBoundRadix, bool showNamespaces, bool useLanguageKeywords, bool hideSystemNamespace)
    {
        ArrayBoundRadix = arrayBoundRadix;
        ShowNamespaces = showNamespaces;
        UseLanguageKeywords = useLanguageKeywords;
        HideSystemNamespace = hideSystemNamespace;
    }
}