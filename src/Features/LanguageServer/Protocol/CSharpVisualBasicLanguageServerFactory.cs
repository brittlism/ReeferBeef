﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Newtonsoft.Json;
using StreamJsonRpc;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    [Export(typeof(ILanguageServerFactory)), Shared]
    internal class CSharpVisualBasicLanguageServerFactory : ILanguageServerFactory
    {
        private readonly AbstractLspServiceProvider _lspServiceProvider;

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CSharpVisualBasicLanguageServerFactory(
            CSharpVisualBasicLspServiceProvider lspServiceProvider)
        {
            _lspServiceProvider = lspServiceProvider;
        }

        public AbstractLanguageServer<RequestContext> Create(
            JsonRpc jsonRpc,
            JsonSerializer jsonSerializer,
            ICapabilitiesProvider capabilitiesProvider,
            WellKnownLspServerKinds serverKind,
            AbstractLspLogger logger,
            HostServices hostServices)
        {
            var server = new RoslynLanguageServer(
                _lspServiceProvider,
                jsonRpc,
                jsonSerializer,
                capabilitiesProvider,
                logger,
                hostServices,
                ProtocolConstants.RoslynLspLanguages,
                serverKind);

            return server;
        }
    }
}
