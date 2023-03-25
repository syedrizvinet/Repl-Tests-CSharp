﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using CSharpRepl.Services.Extensions;
using CSharpRepl.Services.Roslyn.Formatting.CustomObjectFormatters;
using CSharpRepl.Services.Roslyn.Formatting.Rendering;
using CSharpRepl.Services.SyntaxHighlighting;
using CSharpRepl.Services.Theming;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Spectre.Console;

namespace CSharpRepl.Services.Roslyn.Formatting;

internal sealed partial class PrettyPrinter
{
    private const int NumberRadix = 10;

    private static readonly ICustomObjectFormatter[] customObjectFormatters = new ICustomObjectFormatter[]
    {
        IEnumerableFormatter.Instance,
        TypeFormatter.Instance,
        MethodInfoFormatter.Instance,
        TupleFormatter.Instance,
        KeyValuePairFormatter.Instance,
        GuidFormatter.Instance
    };

    private readonly TypeNameFormatter typeNameFormatter;
    private readonly PrimitiveFormatter primitiveFormatter;
    private readonly MemberFilter filter = new();
    private readonly IAnsiConsole console;
    private readonly SyntaxHighlighter syntaxHighlighter;
    private readonly Configuration config;

    public StyledStringSegment NullLiteral => primitiveFormatter.NullLiteral;

    public PrettyPrinter(IAnsiConsole console, SyntaxHighlighter syntaxHighlighter, Configuration config)
    {
        this.primitiveFormatter = new PrimitiveFormatter(syntaxHighlighter);
        this.typeNameFormatter = new TypeNameFormatter(syntaxHighlighter);
        this.console = console;
        this.syntaxHighlighter = syntaxHighlighter;
        this.config = config;
    }

    public FormattedObject FormatObject(object? obj, Level level)
    {
        return obj switch
        {
            null => new FormattedObject(NullLiteral.ToParagraph(), value: null),

            // when detailed is true, don't show the escaped string (i.e. interpret the escape characters, via displaying to console)
            string str when level == Level.FirstDetailed => new FormattedObject(
                new Paragraph(LengthLimiting.LimitLength(str, level, console.Profile)),
                value: str),

            //call stack for compilation error exception is useless
            CompilationErrorException compilationErrorException => new FormattedObject(new Paragraph(compilationErrorException.Message), value: null),

            Exception exception => new FormattedObject(FormatException(exception, level).ToParagraph(), value: exception),

            _ => new FormattedObject(FormatObjectToRenderable(obj, level), obj)
        };
    }

    public StyledString FormatTypeName(Type type, bool showNamespaces, bool useLanguageKeywords, bool hideSystemNamespace = false, IList<string>? tupleNames = null)
        => typeNameFormatter.FormatTypeName(
                type,
                new TypeNameFormatterOptions(
                    arrayBoundRadix: NumberRadix,
                    showNamespaces,
                    useLanguageKeywords,
                    hideSystemNamespace),
                tupleNames);

    public StyledString FormatObjectToText(object? obj, Level level, bool? quoteStringsAndCharacters = null)
        => FormatObjectSafe(
            obj,
            level,
            quoteStringsAndCharacters,
            customObjectFormat: (customFormatter, obj, level, formatter) => customFormatter.FormatToText(obj, level, formatter),
            styledStringToResult: styledString => styledString,
            styledStringSegmentToResult: styledStringSegment => styledStringSegment);

    public FormattedObjectRenderable FormatObjectToRenderable(object? obj, Level level)
        => FormatObjectSafe(
            obj,
            level,
            quoteStringsAndCharacters: null,
            customObjectFormat: (customFormatter, obj, level, formatter) => customFormatter.FormatToRenderable(obj, level, formatter),
            styledStringToResult: styledString => new FormattedObjectRenderable(styledString.ToParagraph(), renderOnNewLine: false),
            styledStringSegmentToResult: styledStringSegment => new FormattedObjectRenderable(styledStringSegment.ToParagraph(), renderOnNewLine: false));

    private TResult FormatObjectSafe<TResult>(
        object? obj,
        Level level,
        bool? quoteStringsAndCharacters,
        Func<ICustomObjectFormatter, object, Level, Formatter, TResult> customObjectFormat,
        Func<StyledString, TResult> styledStringToResult,
        Func<StyledStringSegment, TResult> styledStringSegmentToResult)
    {
        if (obj is null)
        {
            return styledStringSegmentToResult(NullLiteral);
        }

        Debug.Assert(obj is not StyledString);

        try
        {
            var primitiveOptions = GetPrimitiveOptions(quoteStringsAndCharacters ?? true);
            var primitive = primitiveFormatter.FormatPrimitive(obj, primitiveOptions);
            if (primitive.TryGet(out var primitiveValue))
            {
                var result = LengthLimiting.LimitLength(primitiveValue, level, console.Profile);
                return styledStringSegmentToResult(result);
            }

            var type = obj.GetType();
            if (customObjectFormatters.FirstOrDefault(f => f.IsApplicable(obj)).TryGet(out var customFormatter))
            {
                //custom formatters handle length limiting on it's own
                return customObjectFormat(customFormatter, obj, level, new Formatter(this, syntaxHighlighter, console.Profile));
            }

            if (ObjectFormatterHelpers.GetApplicableDebuggerDisplayAttribute(type)?.Value is { } debuggerDisplayFormat)
            {
                var result = LengthLimiting.LimitLength(FormatWithEmbeddedExpressions(debuggerDisplayFormat, obj, level), level, console.Profile);
                var formattedValue = result;
                return styledStringToResult(formattedValue);
            }

            if (ObjectFormatterHelpers.HasOverriddenToString(type))
            {
                try
                {
                    var result = LengthLimiting.LimitLength(obj.ToString(), level, console.Profile);
                    return styledStringSegmentToResult(result);
                }
                catch (Exception ex)
                {
                    return styledStringToResult(GetValueRetrievalExceptionText(ex, level));
                }
            }

            var typeNameOptions = GetTypeNameOptions(level);
            return styledStringToResult(typeNameFormatter.FormatTypeName(type, typeNameOptions));
        }
        catch
        {
            try
            {
                var result = LengthLimiting.LimitLength(obj.ToString(), level, console.Profile) ?? "";
                return styledStringSegmentToResult(result);
            }
            catch (Exception ex)
            {
                return styledStringToResult(GetValueRetrievalExceptionText(ex, level));
            }
        }
    }

    private StyledString FormatWithEmbeddedExpressions(string format, object obj, Level level)
    {
        var sb = new StyledStringBuilder();
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i++];
            if (c == '{')
            {
                if (i >= 2 && format[i - 2] == '\\')
                {
                    sb.Append('{');
                }
                else
                {
                    int expressionEnd = format.IndexOf('}', i);

                    string memberName;
                    if (expressionEnd == -1 || (memberName = ObjectFormatterHelpers.ParseSimpleMemberName(format, i, expressionEnd, out bool noQuotes, out bool callableOnly)) == null)
                    {
                        // the expression isn't properly formatted
                        sb.Append(format.AsSpan(i - 1, format.Length - i + 1).ToString());
                        break;
                    }

                    var member = ObjectFormatterHelpers.ResolveMember(obj, memberName, callableOnly);
                    if (member == null)
                    {
                        sb.Append(GetErrorText($"{(callableOnly ? "Method" : "Member")} '{memberName}' not found"));
                    }
                    else
                    {
                        var value = ObjectFormatterHelpers.GetMemberValue(obj, member, out var exception);
                        if (exception != null)
                        {
                            sb.Append(GetValueRetrievalExceptionText(exception, level));
                        }
                        else
                        {
                            sb.Append(FormatObjectToText(value, level.Increment(), quoteStringsAndCharacters: !noQuotes));
                        }
                    }
                    i = expressionEnd + 1;
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToStyledString();
    }

    private PrimitiveFormatterOptions GetPrimitiveOptions(bool quoteStringsAndCharacters) => new(
        numberRadix: NumberRadix,
        includeCodePoints: false,
        quoteStringsAndCharacters: quoteStringsAndCharacters,
        escapeNonPrintableCharacters: true,
        cultureInfo: CultureInfo.CurrentUICulture);

    private TypeNameFormatterOptions GetTypeNameOptions(Level level) => new(
        arrayBoundRadix: NumberRadix,
        showNamespaces: level == Level.FirstDetailed,
        useLanguageKeywords: true,
        hideSystemNamespace: false);

    public StyledString GetValueRetrievalExceptionText(Exception exception, Level level)
       => GetErrorText(typeNameFormatter.FormatTypeName(exception.GetType(), GetTypeNameOptions(level)));

    private StyledString GetErrorText(StyledString message)
    {
        var sb = new StyledStringBuilder();
        sb.Append("!<", style: new Style(foreground: Color.Red));
        sb.Append(message);
        sb.Append('>', style: new Style(foreground: Color.Red));
        return sb.ToStyledString();
    }
}