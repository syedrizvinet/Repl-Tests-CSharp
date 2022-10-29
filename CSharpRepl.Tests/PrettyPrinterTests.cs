﻿using Xunit;
using CSharpRepl.Services.Roslyn;
using System.Collections.Generic;
using static System.Environment;
using System.Text;

namespace CSharpRepl.Tests;

public class PrettyPrinterTests
{
    [Theory]
    [MemberData(nameof(FormatObjectInputs))]
    public void FormatObject_ObjectInput_PrintsOutput(object obj, bool showDetails, string expectedResult)
    {
        var prettyPrinted = PrettyPrinter.Instance.FormatObject(obj, showDetails);
        Assert.Equal(expectedResult, prettyPrinted);
    }

    public static IEnumerable<object[]> FormatObjectInputs = new[]
    {
        new object[] { null, false, null },
        new object[] { null, true, null },
        new object[] { @"""hello world""", false, @"""\""hello world\"""""},
        new object[] { @"""hello world""", true, @"""hello world"""},
        new object[] { "a\nb", false, @"""a\nb"""},
        new object[] { "a\nb", true, "a\nb"},
        new object[] { new[] { 1, 2, 3 }, false, "int[3] { 1, 2, 3 }"},
        new object[] { new[] { 1, 2, 3 }, true, $"int[3] {"{"}{NewLine}  1,{NewLine}  2,{NewLine}  3{NewLine}{"}"}{NewLine}"},
        new object[] { Encoding.UTF8, true, "System.Text.UTF8Encoding+UTF8EncodingSealed"},
    };
}
