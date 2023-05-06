﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

//Modified copy of https://github.com/dotnet/roslyn/blob/main/src/Workspaces/Core/Portable/Shared/Utilities/DocumentationComment.cs

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Xml;
using CSharpRepl.Services.Extensions;
using CSharpRepl.Services.SyntaxHighlighting;
using Microsoft.CodeAnalysis;
using PrettyPrompt.Highlighting;
using XmlNames = CSharpRepl.Services.Roslyn.DocumentationCommentXmlNames;

namespace CSharpRepl.Services.Roslyn;

/// <summary>
/// A documentation comment derived from either source text or metadata.
/// </summary>
internal sealed class DocumentationComment
{
    private static readonly ThreadLocal<StringBuilder> stringBuilder = new(() => new());

    private readonly Dictionary<string, FormattedString> _parameterTexts = new();
    private readonly Dictionary<string, FormattedString> _typeParameterTexts = new();
    private readonly Dictionary<string, ImmutableArray<FormattedString>> _exceptionTexts = new();

    /// <summary>
    /// True if an error occurred when parsing.
    /// </summary>
    public bool HadXmlParseError { get; private set; }

    /// <summary>
    /// The full XML text of this tag.
    /// </summary>
    public string FullXmlFragment { get; private set; }

    /// <summary>
    /// The text in the &lt;example&gt; tag. Null if no tag existed.
    /// </summary>
    public FormattedString ExampleText { get; private set; }

    /// <summary>
    /// The text in the &lt;summary&gt; tag. Null if no tag existed.
    /// </summary>
    public FormattedString SummaryText { get; private set; }

    /// <summary>
    /// The text in the &lt;returns&gt; tag. Null if no tag existed.
    /// </summary>
    public FormattedString ReturnsText { get; private set; }

    /// <summary>
    /// The text in the &lt;value&gt; tag. Null if no tag existed.
    /// </summary>
    public FormattedString ValueText { get; private set; }

    /// <summary>
    /// The text in the &lt;remarks&gt; tag. Null if no tag existed.
    /// </summary>
    public FormattedString RemarksText { get; private set; }

    /// <summary>
    /// The names of items in &lt;param&gt; tags.
    /// </summary>
    public ImmutableArray<string> ParameterNames { get; private set; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// The names of items in &lt;typeparam&gt; tags.
    /// </summary>
    public ImmutableArray<string> TypeParameterNames { get; private set; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// The types of items in &lt;exception&gt; tags.
    /// </summary>
    public ImmutableArray<string> ExceptionTypes { get; private set; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// The item named in the &lt;completionlist&gt; tag's cref attribute.
    /// Null if the tag or cref attribute didn't exist.
    /// </summary>
    public string? CompletionListCref { get; private set; }

    /// <summary>
    /// Used for <see cref="CommentBuilder.TrimEachLine"/> method, to prevent new allocation of string
    /// </summary>
    private static readonly string[] s_NewLineAsStringArray = new string[] { "\n" };

    private DocumentationComment(string fullXmlFragment)
    {
        FullXmlFragment = fullXmlFragment;
    }

    /// <summary>
    /// Cache of the most recently parsed fragment and the resulting DocumentationComment
    /// </summary>
    private static volatile DocumentationComment? s_cacheLastXmlFragmentParse;

    /// <summary>
    /// Parses and constructs a <see cref="DocumentationComment" /> from the given fragment of XML.
    /// </summary>
    /// <param name="xml">The fragment of XML to parse.</param>
    /// <returns>A DocumentationComment instance.</returns>
    public static DocumentationComment FromXmlFragment(string? xml, SemanticModel semanticModel, SyntaxHighlighter highlighter)
    {
        if (xml is null) return new DocumentationComment("");

        var result = s_cacheLastXmlFragmentParse;
        if (result == null || result.FullXmlFragment != xml)
        {
            // Cache miss
            result = CommentBuilder.Parse(xml, semanticModel, highlighter);
            s_cacheLastXmlFragmentParse = result;
        }

        return result;
    }

    /// <summary>
    /// Helper class for parsing XML doc comments. Encapsulates the state required during parsing.
    /// </summary>
    private class CommentBuilder
    {
        private static readonly ThreadLocal<XmlFragmentParser> parser = new(() => new());

        private readonly DocumentationComment _comment;
        private readonly SemanticModel _semanticModel;
        private readonly SyntaxHighlighter _highlighter;
        private ImmutableArray<string>.Builder? _parameterNamesBuilder;
        private ImmutableArray<string>.Builder? _typeParameterNamesBuilder;
        private ImmutableArray<string>.Builder? _exceptionTypesBuilder;
        private Dictionary<string, ImmutableArray<FormattedString>.Builder>? _exceptionTextBuilders;

        /// <summary>
        /// Parse and construct a <see cref="DocumentationComment" /> from the given fragment of XML.
        /// </summary>
        /// <param name="xml">The fragment of XML to parse.</param>
        /// <returns>A DocumentationComment instance.</returns>
        public static DocumentationComment Parse(string xml, SemanticModel semanticModel, SyntaxHighlighter highlighter)
        {
            try
            {
                return new CommentBuilder(xml, semanticModel, highlighter).ParseInternal(xml);
            }
            catch (Exception)
            {
                // It would be nice if we only had to catch XmlException to handle invalid XML
                // while parsing doc comments. Unfortunately, other exceptions can also occur,
                // so we just catch them all. See Dev12 Bug 612456 for an example.
                return new DocumentationComment(xml) { HadXmlParseError = true };
            }
        }

        private CommentBuilder(string xml, SemanticModel semanticModel, SyntaxHighlighter highlighter)
        {
            _comment = new DocumentationComment(xml);
            _semanticModel = semanticModel;
            _highlighter = highlighter;
        }

        private DocumentationComment ParseInternal(string xml)
        {
            parser.Value!.ParseFragment(xml, ParseCallback, this);

            if (_exceptionTextBuilders != null)
            {
                foreach (var typeAndBuilderPair in _exceptionTextBuilders)
                {
                    _comment._exceptionTexts.Add(typeAndBuilderPair.Key, typeAndBuilderPair.Value.ToImmutable());
                }
            }

            _comment.ParameterNames = _parameterNamesBuilder == null ? ImmutableArray<string>.Empty : _parameterNamesBuilder.ToImmutable();
            _comment.TypeParameterNames = _typeParameterNamesBuilder == null ? ImmutableArray<string>.Empty : _typeParameterNamesBuilder.ToImmutable();
            _comment.ExceptionTypes = _exceptionTypesBuilder == null ? ImmutableArray<string>.Empty : _exceptionTypesBuilder.ToImmutable();

            return _comment;
        }

        private static void ParseCallback(XmlReader reader, CommentBuilder builder)
            => builder.ParseCallback(reader);

        // Find the shortest whitespace prefix and trim it from all the lines
        // Before:
        //  <summary>
        //  Line1
        //  <code>
        //     Line2
        //   Line3
        //  </code>
        //  </summary>
        // After:
        //<summary>
        //Line1
        //<code>
        //   Line2
        // Line3
        //</code>
        //</summary>
        //
        // We preserve the formatting to let the AbstractDocumentationCommentFormattingService get the unmangled
        // <code> blocks.
        // AbstractDocumentationCommentFormattingService will normalize whitespace for non-code element later.
        private static string TrimEachLine(string text)
        {
            var lines = text.Split(s_NewLineAsStringArray, StringSplitOptions.RemoveEmptyEntries);

            var maxPrefix = int.MaxValue;
            foreach (var line in lines)
            {
                var firstNonWhitespaceOffset = GetFirstNonWhitespaceOffset(line);

                // Don't include all-whitespace lines in the calculation
                // They'll be completely trimmed
                if (firstNonWhitespaceOffset == null)
                    continue;

                // note: this code presumes all whitespace should be treated uniformly (for example that a tab and
                // a space are equivalent).  If that turns out to be an issue we will need to revise this to determine
                // an appropriate strategy for trimming here.
                maxPrefix = Math.Min(maxPrefix, firstNonWhitespaceOffset.Value);
            }

            if (maxPrefix == int.MaxValue)
                return string.Empty;

            var builder = stringBuilder.Value!;
            builder.Clear();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (i != 0)
                    builder.AppendLine();

                var trimmedLine = line.TrimEnd();
                if (trimmedLine.Length != 0)
                    builder.Append(trimmedLine, maxPrefix, trimmedLine.Length - maxPrefix);
            }

            return builder.ToString();

            static int? GetFirstNonWhitespaceOffset(string line)
            {
                for (var i = 0; i < line.Length; i++)
                {
                    if (!char.IsWhiteSpace(line[i]))
                    {
                        return i;
                    }
                }
                return null;
            }
        }

        private void ParseCallback(XmlReader reader)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var localName = reader.LocalName;
                if (XmlNames.ElementEquals(localName, XmlNames.ExampleElementName) && _comment.ExampleText == null)
                {
                    _comment.ExampleText = CommentInnerBuilder.Parse(TrimEachLine(reader.ReadInnerXml()), _semanticModel, _highlighter);
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.SummaryElementName) && _comment.SummaryText == null)
                {
                    _comment.SummaryText = CommentInnerBuilder.Parse(TrimEachLine(reader.ReadInnerXml()), _semanticModel, _highlighter);
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.ReturnsElementName) && _comment.ReturnsText == null)
                {
                    _comment.ReturnsText = CommentInnerBuilder.Parse(TrimEachLine(reader.ReadInnerXml()), _semanticModel, _highlighter);
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.ValueElementName) && _comment.ValueText == null)
                {
                    _comment.ValueText = CommentInnerBuilder.Parse(TrimEachLine(reader.ReadInnerXml()), _semanticModel, _highlighter);
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.RemarksElementName) && _comment.RemarksText == null)
                {
                    _comment.RemarksText = CommentInnerBuilder.Parse(TrimEachLine(reader.ReadInnerXml()), _semanticModel, _highlighter);
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.ParameterElementName))
                {
                    var name = reader.GetAttribute(XmlNames.NameAttributeName);
                    var paramText = CommentInnerBuilder.Parse(TrimEachLine(reader.ReadInnerXml()), _semanticModel, _highlighter);

                    if (!string.IsNullOrWhiteSpace(name) && !_comment._parameterTexts.ContainsKey(name))
                    {
                        (_parameterNamesBuilder ??= ImmutableArray.CreateBuilder<string>()).Add(name);
                        _comment._parameterTexts.Add(name, paramText);
                    }
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.TypeParameterElementName))
                {
                    var name = reader.GetAttribute(XmlNames.NameAttributeName);
                    var typeParamText = CommentInnerBuilder.Parse(TrimEachLine(reader.ReadInnerXml()), _semanticModel, _highlighter);

                    if (!string.IsNullOrWhiteSpace(name) && !_comment._typeParameterTexts.ContainsKey(name))
                    {
                        (_typeParameterNamesBuilder ??= ImmutableArray.CreateBuilder<string>()).Add(name);
                        _comment._typeParameterTexts.Add(name, typeParamText);
                    }
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.ExceptionElementName))
                {
                    var type = reader.GetAttribute(XmlNames.CrefAttributeName);
                    var exceptionText = CommentInnerBuilder.Parse(reader.ReadInnerXml(), _semanticModel, _highlighter);

                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        if (_exceptionTextBuilders == null || !_exceptionTextBuilders.ContainsKey(type))
                        {
                            (_exceptionTypesBuilder ??= ImmutableArray.CreateBuilder<string>()).Add(type);
                            (_exceptionTextBuilders ??= new Dictionary<string, ImmutableArray<FormattedString>.Builder>()).Add(type, ImmutableArray.CreateBuilder<FormattedString>());
                        }

                        _exceptionTextBuilders[type].Add(exceptionText);
                    }
                }
                else if (XmlNames.ElementEquals(localName, XmlNames.CompletionListElementName))
                {
                    var cref = reader.GetAttribute(XmlNames.CrefAttributeName);
                    if (!string.IsNullOrWhiteSpace(cref))
                    {
                        _comment.CompletionListCref = cref;
                    }

                    reader.ReadInnerXml();
                }
                else
                {
                    // This is an element we don't handle. Skip it.
                    reader.Read();
                }
            }
            else
            {
                // We came across something that isn't a start element, like a block of text.
                // Skip it.
                reader.Read();
            }
        }
    }

    private class CommentInnerBuilder
    {
        private static readonly ThreadLocal<XmlFragmentParser> parser = new(() => new());

        private readonly FormattedStringBuilder text = new();
        private readonly SemanticModel semanticModel;
        private readonly SyntaxHighlighter highlighter;

        public CommentInnerBuilder(SemanticModel semanticModel, SyntaxHighlighter highlighter)
        {
            this.semanticModel = semanticModel;
            this.highlighter = highlighter;
        }

        public static FormattedString Parse(string xml, SemanticModel semanticModel, SyntaxHighlighter highlighter)
        {
            try
            {
                return new CommentInnerBuilder(semanticModel, highlighter).ParseInternal(xml);
            }
            catch (Exception)
            {
                return FormattedString.Empty;
            }
        }

        private FormattedString ParseInternal(string xml)
        {
            parser.Value!.ParseFragment(xml, ParseCallback, this);
            var result = text.ToFormattedString();
            text.Clear();
            return result;
        }

        private static void ParseCallback(XmlReader reader, CommentInnerBuilder builder)
            => builder.ParseCallback(reader);

        private void ParseCallback(XmlReader reader)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                var localName = reader.LocalName;
                if (XmlNames.ElementEquals(localName, XmlNames.SeeElementName))
                {
                    var cref = reader.GetAttribute(XmlNames.CrefAttributeName);
                    if (cref != null)
                    {
                        if (ParseCRef(cref))
                        {
                            reader.Read();
                            return;
                        }
                    }

                    var langword = reader.GetAttribute(XmlNames.LangwordAttributeName);
                    if (langword != null)
                    {
                        text.Append(langword, highlighter.KeywordFormat);
                        reader.Read();
                        return;
                    }

                    //Debug.Fail("unexpected case");
                    text.Append(reader.ReadInnerXml());
                }
                else
                {
                    // This is an element we don't handle.
                    text.Append(reader.ReadInnerXml());
                }
            }
            else
            {
                // We came across something that isn't a start element, like a block of text.
                text.Append(reader.Value);
                reader.Read();
            }
        }

        private bool ParseCRef(string cref)
        {
            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, semanticModel.Compilation);
            if (symbol is null)
            {
                if (cref.StartsWith("!:")) //invalid
                {
                    text.Append(cref[2..]);
                    return true;
                }

                return false;
            }

            var displayFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
                .RemoveMemberOptions(SymbolDisplayMemberOptions.IncludeType)
                .RemoveKindOptions(SymbolDisplayKindOptions.IncludeMemberKeyword);
            foreach (var part in symbol.ToDisplayParts(displayFormat))
            {
                var partText = part.ToString();
                var classification = RoslynExtensions.SymbolDisplayPartKindToClassificationTypeName(part.Kind);
                if (highlighter.TryGetFormat(classification, out var format))
                {
                    text.Append(partText, format);
                }
                else
                {
                    text.Append(partText);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Returns the text for a given parameter, or null if no documentation was given for the parameter.
    /// </summary>
    public FormattedString GetParameterText(string parameterName)
    {
        return _parameterTexts.TryGetValue(parameterName, out var text) ? text : FormattedString.Empty;
    }

    /// <summary>
    /// Returns the text for a given type parameter, or null if no documentation was given for the type parameter.
    /// </summary>
    public FormattedString GetTypeParameterText(string typeParameterName)
    {
        return _typeParameterTexts.TryGetValue(typeParameterName, out var text) ? text : FormattedString.Empty;
    }

    /// <summary>
    /// Returns the texts for < a given exception, or an empty <see cref="ImmutableArray"/> if no documentation was given for the exception.
    /// </summary>
    public ImmutableArray<FormattedString> GetExceptionTexts(string exceptionName)
    {
        _exceptionTexts.TryGetValue(exceptionName, out var texts);

        if (texts.IsDefault)
        {
            // If the exception wasn't found, TryGetValue will set "texts" to a default value.
            // To be friendly, we want to return an empty array rather than a null array.
            texts = ImmutableArray.Create<FormattedString>();
        }

        return texts;
    }

    /// <summary>
    /// An empty comment.
    /// </summary>
    public static readonly DocumentationComment Empty = new(string.Empty);
}