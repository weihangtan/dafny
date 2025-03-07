﻿using Microsoft.Dafny.LanguageServer.Language;
using Microsoft.Dafny.LanguageServer.Language.Symbols;
using Microsoft.Dafny.LanguageServer.Workspace.Notifications;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Boogie;
using Microsoft.Extensions.Logging;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Microsoft.Dafny.LanguageServer.Workspace {
  /// <summary>
  /// Text document loader implementation that offloads the whole load procedure on one dedicated
  /// thread with a stack size of 256MB. Since only one thread is used, document loading is implicitely synchronized.
  /// The verification runs on the calling thread.
  /// </summary>
  /// <remarks>
  /// The increased stack size is necessary to solve the issue https://github.com/dafny-lang/dafny/issues/1447.
  /// </remarks>
  public class TextDocumentLoader : ITextDocumentLoader {

    private readonly IDafnyParser parser;
    private readonly ISymbolResolver symbolResolver;
    private readonly ISymbolTableFactory symbolTableFactory;
    private readonly IGhostStateDiagnosticCollector ghostStateDiagnosticCollector;
    protected readonly ICompilationStatusNotificationPublisher statusPublisher;
    protected readonly ILoggerFactory loggerFactory;

    protected TextDocumentLoader(
      ILoggerFactory loggerFactory,
      IDafnyParser parser,
      ISymbolResolver symbolResolver,
      ISymbolTableFactory symbolTableFactory,
      IGhostStateDiagnosticCollector ghostStateDiagnosticCollector,
      ICompilationStatusNotificationPublisher statusPublisher) {
      this.parser = parser;
      this.symbolResolver = symbolResolver;
      this.symbolTableFactory = symbolTableFactory;
      this.ghostStateDiagnosticCollector = ghostStateDiagnosticCollector;
      this.statusPublisher = statusPublisher;
      this.loggerFactory = loggerFactory;
    }

    public static TextDocumentLoader Create(
      IDafnyParser parser,
      ISymbolResolver symbolResolver,
      ISymbolTableFactory symbolTableFactory,
      IGhostStateDiagnosticCollector ghostStateDiagnosticCollector,
      ICompilationStatusNotificationPublisher statusPublisher,
      ILoggerFactory loggerFactory
      ) {
      return new TextDocumentLoader(loggerFactory, parser, symbolResolver, symbolTableFactory, ghostStateDiagnosticCollector, statusPublisher);
    }

    public IdeState CreateUnloaded(DafnyProject project) {
      return CreateDocumentWithEmptySymbolTable(project, ImmutableDictionary<Uri, IReadOnlyList<Diagnostic>>.Empty);
    }

    public async Task<CompilationAfterParsing> LoadAsync(DafnyOptions options, Compilation compilation,
      CancellationToken cancellationToken) {
#pragma warning disable CS1998
      return await await DafnyMain.LargeStackFactory.StartNew(
        async () => LoadInternal(options, compilation, cancellationToken), cancellationToken
#pragma warning restore CS1998
        );
    }

    private CompilationAfterParsing LoadInternal(DafnyOptions options, Compilation compilation,
      CancellationToken cancellationToken) {
      var project = compilation.Project;
      var errorReporter = new DiagnosticErrorReporter(options, project.Uri);
      statusPublisher.SendStatusNotification(compilation, CompilationStatus.Parsing);
      var program = parser.Parse(project, errorReporter, cancellationToken);
      var compilationAfterParsing = new CompilationAfterParsing(compilation, program, errorReporter.AllDiagnosticsCopy);
      if (errorReporter.HasErrors) {
        statusPublisher.SendStatusNotification(compilation, CompilationStatus.ParsingFailed);
        return compilationAfterParsing;
      }

      statusPublisher.SendStatusNotification(compilation, CompilationStatus.ResolutionStarted);
      try {
        var compilationUnit = symbolResolver.ResolveSymbols(project, program, cancellationToken);
        var legacySymbolTable = symbolTableFactory.CreateFrom(compilationUnit, cancellationToken);

        var newSymbolTable = errorReporter.HasErrors
          ? null
          : symbolTableFactory.CreateFrom(program, compilationAfterParsing, cancellationToken);
        if (errorReporter.HasErrors) {
          statusPublisher.SendStatusNotification(compilation, CompilationStatus.ResolutionFailed);
        } else {
          statusPublisher.SendStatusNotification(compilation, CompilationStatus.CompilationSucceeded);
        }

        var ghostDiagnostics = ghostStateDiagnosticCollector.GetGhostStateDiagnostics(legacySymbolTable, cancellationToken);

        return new CompilationAfterResolution(compilationAfterParsing,
          errorReporter.AllDiagnosticsCopy,
          newSymbolTable,
          legacySymbolTable,
          ghostDiagnostics
        );
      } catch (OperationCanceledException) {
        return compilationAfterParsing;
      }
    }

    private IdeState CreateDocumentWithEmptySymbolTable(DafnyProject project,
      IReadOnlyDictionary<Uri, IReadOnlyList<Diagnostic>> resolutionDiagnostics) {
      var dafnyOptions = DafnyOptions.Default;
      return new IdeState(
        new Compilation(0, project),
        new EmptyNode(),
        resolutionDiagnostics,
        SymbolTable.Empty(),
        SignatureAndCompletionTable.Empty(dafnyOptions, project),
        new Dictionary<ImplementationId, IdeImplementationView>(),
        Array.Empty<Counterexample>(),
        false,
        ImmutableDictionary<Uri, IReadOnlyList<Range>>.Empty,
      null
      );
    }
  }
}


public record ImplementationId(Uri Uri, Position Position, string Name);
