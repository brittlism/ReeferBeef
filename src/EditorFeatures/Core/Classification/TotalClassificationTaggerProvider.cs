﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.CodeAnalysis.Workspaces;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace Microsoft.CodeAnalysis.Classification;

[Export(typeof(IViewTaggerProvider))]
[TagType(typeof(IClassificationTag))]
[Microsoft.VisualStudio.Utilities.ContentType(ContentTypeNames.RoslynContentType)]
[method: ImportingConstructor]
[method: SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
internal sealed class TotalClassificationTaggerProvider(
    IThreadingContext threadingContext,
    ClassificationTypeMap typeMap,
    IGlobalOptionService globalOptions,
    [Import(AllowDefault = true)] ITextBufferVisibilityTracker? visibilityTracker,
    IAsynchronousOperationListenerProvider listenerProvider) : IViewTaggerProvider
{
    private readonly SyntacticClassificationTaggerProvider _syntacticTaggerProvider = new(threadingContext, typeMap, globalOptions, listenerProvider);
    private readonly SemanticClassificationViewTaggerProvider _semanticTaggerProvider = new(threadingContext, typeMap, globalOptions, visibilityTracker, listenerProvider);
    private readonly EmbeddedLanguageClassificationViewTaggerProvider _embeddedTaggerProvider = new(threadingContext, typeMap, globalOptions, visibilityTracker, listenerProvider);

    public ITagger<T>? CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        var syntacticTagger = _syntacticTaggerProvider.CreateTagger<T>(buffer);
        var semanticTagger = _semanticTaggerProvider.CreateTagger(textView, buffer);
        var embeddedTagger = _embeddedTaggerProvider.CreateTagger(textView, buffer);

        if (syntacticTagger is not ITagger<IClassificationTag> typedSyntacticTagger)
        {
            (syntacticTagger as IDisposable)?.Dispose();
            semanticTagger.Dispose();
            embeddedTagger.Dispose();
            return null;
        }

        var finalTagger = new TotalClassificationAggregateTagger(typedSyntacticTagger, semanticTagger, embeddedTagger);
        if (finalTagger is not ITagger<T> typedTagger)
        {
            finalTagger.Dispose();
            return null;
        }

        return typedTagger;
    }

    private sealed class TotalClassificationAggregateTagger(
        ITagger<IClassificationTag> syntacticTagger,
        SimpleAggregateTagger<IClassificationTag> semanticTagger,
        SimpleAggregateTagger<IClassificationTag> embeddedTagger)
        : AbstractAggregateTagger<IClassificationTag>(ImmutableArray.Create(syntacticTagger, semanticTagger, embeddedTagger))
    {
        private static readonly Comparison<ITagSpan<IClassificationTag>> s_spanComparison = static (s1, s2) => s1.Span.Start - s2.Span.Start;

        public override SegmentedList<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            // First, get all the syntactic tags.  While they are generally overridden by semantic tags (since semantics
            // allows us to understand better what things like identifiers mean), they do take precedence for certain
            // tags like 'Comments' and 'Excluded Code'.  In those cases we want the classification to 'snap' instantly to
            // the syntactic state, and we do not want things like semantic classifications showing up over that.

            var totalTags = new SegmentedList<ITagSpan<IClassificationTag>>();

            using var _ = Classifier.GetPooledList<ITagSpan<IClassificationTag>>(out var stringLiterals);

            var syntacticSpans = syntacticTagger.GetTags(spans).GetEnumerator();
            var semanticSpans = semanticTagger.GetTags(spans).GetEnumerator();

            //var syntacticIntervalTree = SimpleIntervalTree.Create(TagSpanIntrospector.Instance, syntacticTagger.GetTags(spans));
            //var semanticIntervalTree = SimpleIntervalTree.Create(TagSpanIntrospector.Instance, semanticTagger.GetTags(spans));

            var currentSyntactic = NextOrNull(syntacticSpans);
            var currentSemantic = NextOrNull(semanticSpans);

            while (currentSyntactic != null && currentSemantic != null)
            {
                // as long as we see semantic spans before the next syntactic one, keep adding them.
                if (currentSemantic.Span.Start <= currentSyntactic.Span.Start)
                {
                    totalTags.Add(currentSemantic);
                    currentSemantic = NextOrNull(semanticSpans);
                }
                else
                {
                    // We're on a syntactic span before the next semantic one.

                    // If it's a comment or excluded code, then we want to ignore every semantic classification that
                    // potentially overlaps with it so that semantic classifications don't show up *on top of* them.  We
                    // want commenting out code to feel like' it instantly snaps to that state.
                    if (TryProcessCommentOrExcludedCode())
                        continue;

                    // If we have a string literal of some sort add it to the list to be processed later. We'll want to
                    // compute embedded classifications for them, and have those classifications override the string
                    // literals.
                    if (TryProcessSyntacticStringLiteral())
                        continue;

                    // Normal case.  Just add the syntactic span and continue.
                    totalTags.Add(currentSyntactic);
                    currentSyntactic = NextOrNull(syntacticSpans);
                }
            }

            // Add any remaining semantic spans following the syntactic ones.
            while (currentSemantic != null)
            {
                totalTags.Add(currentSemantic);
                currentSemantic = NextOrNull(semanticSpans);
            }

            // Add any remaining syntacticspans following the semantic ones.
            while (currentSyntactic != null)
            {
                // don't have to worry about comments/excluded code since there are no semantic tags we want to override.
                if (TryProcessSyntacticStringLiteral())
                    continue;

                totalTags.Add(currentSyntactic);
                currentSyntactic = NextOrNull(syntacticSpans);
            }

            // We've added almost all the syntactic and semantic tags (properly skipping any semantic tags that are
            // overridden by comments or excluded code).  All that remains is adding back the string literals we
            // skipped.  However, when we do so, we'll see if those string literals themselves should be overridden
            // by any embedded classifications.
            AddEmbeddedClassifications();

            return totalTags;

            bool TryProcessSyntacticStringLiteral()
            {
                if (currentSyntactic.Tag.ClassificationType.Classification is not ClassificationTypeNames.StringLiteral and not ClassificationTypeNames.VerbatimStringLiteral)
                    return false;

                stringLiterals.Add(currentSyntactic);
                return true;
            }

            bool TryProcessCommentOrExcludedCode()
            {
                if (currentSyntactic.Tag.ClassificationType.Classification is not ClassificationTypeNames.Comment and not ClassificationTypeNames.ExcludedCode)
                    return false;

                // Keep skipping semantic tags that overlaps with this syntactic tag.
                while (currentSemantic != null && currentSemantic.Span.OverlapsWith(currentSyntactic.Span.Span))
                    currentSemantic = NextOrNull(semanticSpans);

                // now add that syntactic span.
                totalTags.Add(currentSyntactic);
                currentSyntactic = NextOrNull(syntacticSpans);

                return true;
            }

            void AddEmbeddedClassifications()
            {
                // nothing to do if we didn't run into any string literals.
                if (stringLiterals.Count == 0)
                    return;

                // Only need to ask for the spans that overlapped the string literals.
                var stringLiteralSpans = new NormalizedSnapshotSpanCollection(stringLiterals.Select(s => s.Span));
                var embeddedClassifications = embeddedTagger.GetTags(stringLiteralSpans);

                // Nothing to do if we got no embedded classifications back.
                if (embeddedClassifications.Count == 0)
                    return;

                // ClassifierHelper.MergeParts requires these to be sorted.
                stringLiterals.Sort(s_spanComparison);
                embeddedClassifications.Sort(s_spanComparison);

                // Call into the helper to merge the string literals and embedded classifications into the final result.
                // The helper will add all the embedded classifications first, then add string literal classifications
                // in the the space between the embedded classifications that were originally classified as a string
                // literal.
                ClassifierHelper.MergeParts<ITagSpan<IClassificationTag>, ClassificationTagSpanIntervalIntrospector>(
                    stringLiterals,
                    embeddedClassifications,
                    totalTags,
                    static tag => tag.Span.Span.ToTextSpan(),
                    static (original, final) => new TagSpan<IClassificationTag>(new SnapshotSpan(original.Span.Snapshot, final.ToSpan()), original.Tag));
            }
        }

        private static ITagSpan<IClassificationTag>? NextOrNull(IEnumerator<ITagSpan<IClassificationTag>> enumerator)
            => enumerator.MoveNext() ? enumerator.Current : null;

        private static ITagSpan<IClassificationTag>? NextOrNull(SegmentedList<ITagSpan<IClassificationTag>>.Enumerator enumerator)
            => enumerator.MoveNext() ? enumerator.Current : null;
    }

    private readonly struct ClassificationTagSpanIntervalIntrospector : IIntervalIntrospector<ITagSpan<IClassificationTag>>
    {
        public int GetStart(ITagSpan<IClassificationTag> value)
            => value.Span.Start;

        public int GetLength(ITagSpan<IClassificationTag> value)
            => value.Span.Length;
    }
}
