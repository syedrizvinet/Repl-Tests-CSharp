﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CSharpRepl.PrettyPromptConfig;
using CSharpRepl.Services;
using CSharpRepl.Services.Roslyn;
using PrettyPrompt;
using PrettyPrompt.Consoles;
using Xunit;

namespace CSharpRepl.Tests;

[Collection(nameof(RoslynServices))]
public class PromptConfigurationTests : IAsyncLifetime
{
    private readonly RoslynServices services;
    private readonly IConsoleEx console;
    private readonly StringBuilder stdout;

    public PromptConfigurationTests()
    {
        var (console, stdout) = FakeConsole.CreateStubbedOutput();
        this.console = console;
        this.stdout = stdout;

        this.services = new RoslynServices(console, new Configuration(), new TestTraceLogger());
    }

    public Task InitializeAsync() => services.WarmUpAsync(Array.Empty<string>());
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [MemberData(nameof(KeyPresses))]
    public void PromptConfiguration_CanCreate(ConsoleKeyInfo keyInfo)
    {
        IPromptCallbacks configuration = new CSharpReplPromptCallbacks(console, services, new Configuration());
        Assert.True(configuration.TryGetKeyPressCallbacks(keyInfo, out var callback));
        callback.Invoke("Console.WriteLine(\"Hi!\");", 0, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PromptConfiguration_Identation(bool shiftPressed)
    {
        IPromptCallbacks configuration = new CSharpReplPromptCallbacks(console, services, new Configuration());
        var enterKey = new KeyPress(new ConsoleKeyInfo('\0', ConsoleKey.Enter, shift: shiftPressed, alt: false, control: false));

        var transformed = await configuration.TransformKeyPressAsync("if (true) {", 11, enterKey, CancellationToken.None);
        Assert.Equal("\n\t", transformed.PastedText);
    }

    public static IEnumerable<object[]> KeyPresses()
    {
        yield return new object[] { new ConsoleKeyInfo('\0', ConsoleKey.F1, shift: false, alt: false, control: false) };
        yield return new object[] { new ConsoleKeyInfo('\0', ConsoleKey.F1, shift: false, alt: false, control: true) };
        yield return new object[] { new ConsoleKeyInfo('\0', ConsoleKey.F9, shift: false, alt: false, control: false) };
        yield return new object[] { new ConsoleKeyInfo('\0', ConsoleKey.F9, shift: false, alt: false, control: true) };
        yield return new object[] { new ConsoleKeyInfo('\0', ConsoleKey.F12, shift: false, alt: false, control: false) };
        yield return new object[] { new ConsoleKeyInfo('\0', ConsoleKey.D, shift: false, alt: false, control: true) };
    }
}