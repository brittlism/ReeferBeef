﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols.Finders
{
    internal class NamedTypeSymbolReferenceFinder : AbstractReferenceFinder<INamedTypeSymbol>
    {
        protected override bool CanFind(INamedTypeSymbol symbol)
            => symbol.TypeKind != TypeKind.Error;

        protected override Task<ImmutableArray<ISymbol>> DetermineCascadedSymbolsAsync(
            INamedTypeSymbol symbol,
            Solution solution,
            FindReferencesSearchOptions options,
            CancellationToken cancellationToken)
        {
            using var _ = ArrayBuilder<ISymbol>.GetInstance(out var result);

            if (symbol.AssociatedSymbol != null)
                Add(result, ImmutableArray.Create(symbol.AssociatedSymbol));

            // cascade to constructors
            Add(result, symbol.Constructors);

            // cascade to destructor
            Add(result, symbol.GetMembers(WellKnownMemberNames.DestructorName));

            return Task.FromResult(result.ToImmutable());
        }

        private static void Add<TSymbol>(ArrayBuilder<ISymbol> result, ImmutableArray<TSymbol> enumerable) where TSymbol : ISymbol
        {
            result.AddRange(enumerable.Cast<ISymbol>());
        }

        protected override async Task<ImmutableArray<Document>> DetermineDocumentsToSearchAsync(
            INamedTypeSymbol symbol,
            Project project,
            IImmutableSet<Document>? documents,
            FindReferencesSearchOptions options,
            CancellationToken cancellationToken)
        {
            var syntaxFacts = project.LanguageServices.GetRequiredService<ISyntaxFactsService>();

            using var _ = ArrayBuilder<Document>.GetInstance(out var result);

            // Named types might be referenced through global aliases, which themselves are then referenced elsewhere.
            var allMatchingGlobalAliasNames = await GetAllMatchingGlobalAliasNamesAsync(project, symbol.Name, symbol.Arity, cancellationToken).ConfigureAwait(false);
            foreach (var globalAliasName in allMatchingGlobalAliasNames)
                result.AddRange(await FindDocumentsAsync(project, documents, cancellationToken, globalAliasName).ConfigureAwait(false));

            var documentsWithName = await FindDocumentsAsync(project, documents, cancellationToken, symbol.Name).ConfigureAwait(false);
            var documentsWithType = await FindDocumentsAsync(project, documents, symbol.SpecialType.ToPredefinedType(), cancellationToken).ConfigureAwait(false);

            var documentsWithAttribute = TryGetNameWithoutAttributeSuffix(symbol.Name, syntaxFacts, out var simpleName)
                ? await FindDocumentsAsync(project, documents, cancellationToken, simpleName).ConfigureAwait(false)
                : ImmutableArray<Document>.Empty;

            var documentsWithGlobalAttributes = await FindDocumentsWithGlobalAttributesAsync(project, documents, cancellationToken).ConfigureAwait(false);

            result.AddRange(documentsWithName);
            result.AddRange(documentsWithType);
            result.AddRange(documentsWithAttribute);
            result.AddRange(documentsWithGlobalAttributes);

            return result.ToImmutable();
        }

        private static bool IsPotentialReference(
            PredefinedType predefinedType,
            ISyntaxFactsService syntaxFacts,
            SyntaxToken token)
        {
            return
                syntaxFacts.TryGetPredefinedType(token, out var actualType) &&
                predefinedType == actualType;
        }

        protected override async ValueTask<ImmutableArray<FinderLocation>> FindReferencesInDocumentAsync(
            INamedTypeSymbol namedType,
            Document document,
            SemanticModel semanticModel,
            FindReferencesSearchOptions options,
            CancellationToken cancellationToken)
        {
            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            using var _ = ArrayBuilder<FinderLocation>.GetInstance(out var initialReferences);

            await AddReferencesToTypeOrGlobalAliasToItAsync(namedType, document, semanticModel, initialReferences, cancellationToken).ConfigureAwait(false);

            var symbolsMatch = GetStandardSymbolsMatchFunction(namedType, findParentNode: null, document.Project.Solution, cancellationToken);
            var suppressionReferences = await FindReferencesInDocumentInsideGlobalSuppressionsAsync(document, semanticModel, namedType, cancellationToken).ConfigureAwait(false);

            var aliasReferences = await FindLocalAliasReferencesAsync(initialReferences, document, semanticModel, symbolsMatch, cancellationToken).ConfigureAwait(false);

            initialReferences.AddRange(suppressionReferences);
            initialReferences.AddRange(aliasReferences);

            return initialReferences.ToImmutable();
        }

        internal static async ValueTask AddReferencesToTypeOrGlobalAliasToItAsync(
            INamedTypeSymbol namedType,
            Document document,
            SemanticModel semanticModel,
            ArrayBuilder<FinderLocation> nonAliasReferences,
            CancellationToken cancellationToken)
        {
            nonAliasReferences.AddRange(await FindNonAliasReferencesAsync(
                namedType, namedType.Name, document, semanticModel, cancellationToken).ConfigureAwait(false));

            var globalAliases = await GetAllMatchingGlobalAliasNamesAsync(document.Project, namedType.Name, namedType.Arity, cancellationToken).ConfigureAwait(false);
            foreach (var globalAlias in globalAliases)
            {
                nonAliasReferences.AddRange(await FindNonAliasReferencesAsync(
                    namedType, globalAlias, document, semanticModel, cancellationToken).ConfigureAwait(false));
            }
        }

        private static async ValueTask<ImmutableArray<FinderLocation>> FindNonAliasReferencesAsync(
            INamedTypeSymbol symbol,
            string name,
            Document document,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            var ordinaryRefs = await FindOrdinaryReferencesAsync(symbol, name, document, semanticModel, cancellationToken).ConfigureAwait(false);
            var attributeRefs = await FindAttributeReferencesAsync(symbol, name, document, semanticModel, cancellationToken).ConfigureAwait(false);
            var predefinedTypeRefs = await FindPredefinedTypeReferencesAsync(symbol, document, semanticModel, cancellationToken).ConfigureAwait(false);

            return ordinaryRefs.Concat(attributeRefs).Concat(predefinedTypeRefs);
        }

        private static ValueTask<ImmutableArray<FinderLocation>> FindOrdinaryReferencesAsync(
            INamedTypeSymbol namedType,
            string name,
            Document document,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            // Get the parent node that best matches what this token represents.  For example, if we have `new a.b()`
            // then the parent node of `b` won't be `a.b`, but rather `new a.b()`.  This will actually cause us to bind
            // to the constructor not the type.  That's a good thing as we don't want these object-creations to
            // associate with the type, but rather with the constructor itself.
            var findParentNode = GetNamedTypeOrConstructorFindParentNodeFunction(document, namedType);
            var symbolsMatch = GetStandardSymbolsMatchFunction(namedType, findParentNode, document.Project.Solution, cancellationToken);

            return FindReferencesInDocumentUsingIdentifierAsync(
                namedType, name, document, semanticModel, symbolsMatch, cancellationToken);
        }

        private static ValueTask<ImmutableArray<FinderLocation>> FindPredefinedTypeReferencesAsync(
            INamedTypeSymbol symbol,
            Document document,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            var predefinedType = symbol.SpecialType.ToPredefinedType();
            if (predefinedType == PredefinedType.None)
            {
                return new ValueTask<ImmutableArray<FinderLocation>>(ImmutableArray<FinderLocation>.Empty);
            }

            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            return FindReferencesInDocumentAsync(document, semanticModel, t =>
                IsPotentialReference(predefinedType, syntaxFacts, t),
                (t, m) => ValueTaskFactory.FromResult((matched: true, reason: CandidateReason.None)),
                cancellationToken);
        }

        private static ValueTask<ImmutableArray<FinderLocation>> FindAttributeReferencesAsync(
            INamedTypeSymbol namedType,
            string name,
            Document document,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            var symbolsMatch = GetStandardSymbolsMatchFunction(namedType, findParentNode: null, document.Project.Solution, cancellationToken);
            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            return TryGetNameWithoutAttributeSuffix(name, syntaxFacts, out var nameWithoutSuffix)
                ? FindReferencesInDocumentUsingIdentifierAsync(namedType, nameWithoutSuffix, document, semanticModel, symbolsMatch, cancellationToken)
                : new ValueTask<ImmutableArray<FinderLocation>>(ImmutableArray<FinderLocation>.Empty);
        }
    }
}
