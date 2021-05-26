﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sharply.Services.Completion
{
    public record CompletionItemWithDescription(CompletionItem Item, Lazy<Task<string>> DescriptionProvider);

    class AutoCompleteService
    {
        private readonly IMemoryCache cache;

        public AutoCompleteService(IMemoryCache cache)
        {
            this.cache = cache;
        }

        public async Task<CompletionItemWithDescription[]> Complete(Document document, string text, int caret)
        {
            if (text != string.Empty && cache.Get<CompletionItemWithDescription[]>(text + caret) is CompletionItemWithDescription[] cached)
                return cached;

            var completions = await CompletionService.GetService(document).GetCompletionsAsync(document, caret).ConfigureAwait(false);
            var completionsWithDescriptions = completions?.Items
                .Select(item => new CompletionItemWithDescription(item, new Lazy<Task<string>>(async () =>
                {
                    var currentText = await document.GetTextAsync().ConfigureAwait(false);
                    var completedText = currentText.Replace(item.Span, item.DisplayText);
                    var completedDocument = document.WithText(completedText);
                    var infoService = QuickInfoService.GetService(completedDocument);
                    var info = await infoService.GetQuickInfoAsync(completedDocument, item.Span.End).ConfigureAwait(false);
                    return info is null
                        ? string.Empty
                        : string.Join(Environment.NewLine, info.Sections.Select(s => s.Text));
                })))
                .ToArray()
                ??
                Array.Empty<CompletionItemWithDescription>();

            cache.Set(text + caret, completionsWithDescriptions, DateTimeOffset.Now.AddMinutes(1));

            return completionsWithDescriptions;
        }
    }
}
