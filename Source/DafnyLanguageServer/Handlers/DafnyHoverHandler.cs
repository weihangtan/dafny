﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Dafny.LanguageServer.Language.Symbols;
using Microsoft.Dafny.LanguageServer.Workspace;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Boogie;
using Microsoft.Dafny.LanguageServer.Language;
using Microsoft.Dafny.LanguageServer.Workspace.Notifications;
using EnsuresDescription = Microsoft.Dafny.ProofObligationDescription.EnsuresDescription;

namespace Microsoft.Dafny.LanguageServer.Handlers {
  public class DafnyHoverHandler : HoverHandlerBase {
    // TODO add the range of the name to the hover.
    private readonly ILogger logger;
    private readonly IProjectDatabase projects;
    private DafnyOptions options;

    private const long RuLimitToBeOverCostly = 10000000;
    private const string OverCostlyMessage =
      " [⚠](https://dafny-lang.github.io/dafny/DafnyRef/DafnyRef#sec-verification-debugging-slow)";

    public DafnyHoverHandler(ILogger<DafnyHoverHandler> logger, IProjectDatabase projects, DafnyOptions options) {
      this.logger = logger;
      this.projects = projects;
      this.options = options;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities) {
      return new HoverRegistrationOptions {
        DocumentSelector = DocumentSelector.ForLanguage("dafny")
      };
    }

    public override async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken) {
      logger.LogDebug("received hover request for {Document}", request.TextDocument);
      var document = await projects.GetResolvedDocumentAsync(request.TextDocument);
      if (document == null) {
        logger.LogWarning("the document {Document} is not loaded", request.TextDocument);
        return null;
      }
      var diagnosticHoverContent = GetDiagnosticsHover(document, request.TextDocument.Uri.ToUri(), request.Position, out var areMethodStatistics);
      if (!document.SignatureAndCompletionTable.TryGetSymbolAt(request.Position, out var symbol)) {
        logger.LogDebug("no symbol was found at {Position} in {Document}", request.Position, request.TextDocument);
      }

      var symbolHoverContent = symbol != null ? CreateSymbolMarkdown(symbol, cancellationToken) : null;
      if (diagnosticHoverContent == null && symbolHoverContent == null) {
        return null;
      }

      // If diagnostics are method diagnostics, we prioritize displaying the symbol information.
      // This makes testing easier and less surprise for the user.
      var hoverContent = areMethodStatistics && symbolHoverContent != null ? "" : (diagnosticHoverContent ?? "");
      hoverContent = symbolHoverContent != null ? hoverContent + (hoverContent != "" ? "  \n" : "") + symbolHoverContent : hoverContent;
      return CreateMarkdownHover(hoverContent);
    }

    class AssertionBatchIndexComparer : IComparer<AssertionBatchIndex> {
      public int Compare(AssertionBatchIndex? key1, AssertionBatchIndex? key2) {
        return key1 == null || key2 == null ? -1 :
          key1.ImplementationIndex < key2.ImplementationIndex ? -1 :
          key1.ImplementationIndex == key2.ImplementationIndex ? key1.RelativeIndex - key2.RelativeIndex : 1;
      }
    }

    private string? GetDiagnosticsHover(IdeState state, Uri uri, Position position, out bool areMethodStatistics) {
      areMethodStatistics = false;
      var uriDiagnostics = state.GetDiagnostics().GetOrDefault(uri, Enumerable.Empty<Diagnostic>).ToList();
      foreach (var diagnostic in uriDiagnostics) {
        if (diagnostic.Range.Contains(position)) {
          string? detail = ErrorRegistry.GetDetail(diagnostic.Code);
          if (detail is not null) {
            return detail;
          }
        }
      }

      if (uriDiagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error && (
            diagnostic.Source == MessageSource.Parser.ToString() ||
            diagnostic.Source == MessageSource.Resolver.ToString()))) {
        return null;
      }

      if (state.VerificationTree == null) {
        return null;
      }
      foreach (var node in state.VerificationTree.Children.OfType<TopLevelDeclMemberVerificationTree>()) {
        if (!node.Range.Contains(position)) {
          continue;
        }

        var assertionBatchCount = node.AssertionBatchCount;
        var information = "";
        var orderedAssertionBatches =
          node.AssertionBatches
            .OrderBy(keyValue => keyValue.Key, new AssertionBatchIndexComparer()).Select(keyValuePair => keyValuePair.Value)
            .ToList();

        foreach (var assertionBatch in orderedAssertionBatches) {
          if (!assertionBatch.Range.Contains(position)) {
            continue;
          }

          var assertionIndex = 0;
          var assertions = assertionBatch.Children.OfType<AssertionVerificationTree>().ToList();
          foreach (var assertionNode in assertions) {
            if (assertionNode.Range.Contains(position) ||
                assertionNode.ImmediatelyRelatedRanges.Any(range => range.Contains(position))) {
              if (information != "") {
                information += "\n\n";
              }
              information += GetAssertionInformation(state, position, assertionNode, assertionBatch, assertionIndex, assertionBatchCount, node);
            }

            assertionIndex++;
          }
        }

        if (information != "") {
          return information;
        }
        // Ok no assertion here. Maybe a method?
        if (node.Position.Line == position.Line && state.Compilation.Project.IsImplicitProject && node.Uri == state.Compilation.Project.Uri) {
          areMethodStatistics = true;
          return GetTopLevelInformation(node, orderedAssertionBatches);
        }
      }

      return null;
    }

    private string GetTopLevelInformation(TopLevelDeclMemberVerificationTree node, List<AssertionBatchVerificationTree> orderedAssertionBatches) {
      int assertionBatchCount = orderedAssertionBatches.Count;
      var information = $"**Verification performance metrics for {node.PrefixedDisplayName}**:\n\n";
      if (!node.Started) {
        information += "_Verification not started yet_  \n";
      } else if (!node.Finished) {
        information += "_Still verifying..._  \n";
      }

      var assertionBatchesToReport =
        orderedAssertionBatches.OrderByDescending(a => a.ResourceCount).Take(3).ToList();
      if (assertionBatchesToReport.Count == 0 && node.Finished) {
        information += "No assertions.";
      } else if (assertionBatchesToReport.Count >= 1) {
        information += $"- Total resource usage: {FormatResourceCount(node.ResourceCount)}";
        if (node.ResourceCount > RuLimitToBeOverCostly) {
          information += OverCostlyMessage;
        }

        information += "  \n";
        if (orderedAssertionBatches.Count == 1) {
          var assertionBatch = AddAssertionBatchDocumentation("assertion batch");
          var numberOfAssertions = orderedAssertionBatches.First().NumberOfAssertions;
          information +=
            $"- Only one {assertionBatch} containing {numberOfAssertions} assertion{(numberOfAssertions == 1 ? "" : "s")}.";
        } else {
          var assertionBatches = AddAssertionBatchDocumentation("assertion batches");
          information += $"- Most costly {assertionBatches}:";
          var result =
            new List<(string index, string line, string numberOfAssertions, string assertionPlural, string
              resourceCount, bool overCostly)>();
          foreach (var costlierAssertionBatch in assertionBatchesToReport) {
            var item = costlierAssertionBatch.Range.Start.Line + 1;
            var overCostly = costlierAssertionBatch.ResourceCount > RuLimitToBeOverCostly;
            result.Add(("#" + costlierAssertionBatch.RelativeNumber, item.ToString(),
              costlierAssertionBatch.Children.Count + "",
              costlierAssertionBatch.Children.Count != 1 ? "s" : "",
              FormatResourceCount(costlierAssertionBatch.ResourceCount), overCostly));
          }

          var maxIndexLength = result.Select(item => item.index.Length).Max();
          var maxLineLength = result.Select(item => item.line.Length).Max();
          var maxNumberOfAssertionsLength = result.Select(item => item.numberOfAssertions.Length).Max();
          var maxAssertionsPluralLength = result.Select(item => item.assertionPlural.Length).Max();
          var maxResourceLength = result.Select(item => item.resourceCount.Length).Max();
          foreach (var (index, line, numberOfAssertions, assertionPlural, resource, overCostly) in result) {
            information +=
              $"  \n  - {index.PadLeft(maxIndexLength)}/{assertionBatchCount}" +
              $" with {numberOfAssertions.PadLeft(maxNumberOfAssertionsLength)} assertion" +
              assertionPlural.PadRight(maxAssertionsPluralLength) +
              $" at line {line.PadLeft(maxLineLength)}, {resource.PadLeft(maxResourceLength)}";
            if (overCostly) {
              information += OverCostlyMessage;
            }
          }
        }
      }

      return information;
    }

    private string GetAssertionInformation(IdeState ideState, Position position, AssertionVerificationTree assertionNode,
      AssertionBatchVerificationTree assertionBatch, int assertionIndex, int assertionBatchCount,
      TopLevelDeclMemberVerificationTree node) {
      var assertCmd = assertionNode.GetAssertion();
      var batchRef = AddAssertionBatchDocumentation("batch");
      var assertionCount = assertionBatch.Children.Count;


      var currentlyHoveringPostcondition =
        assertionNode.GetCounterExample() is ReturnCounterexample returnCounterexample2 &&
          !returnCounterexample2.FailingReturn.tok.GetLspRange().Contains(position);

      var obsolescence = assertionNode.StatusCurrent switch {
        CurrentStatus.Current => "",
        CurrentStatus.Obsolete => "(obsolete) ",
        _ => "(verifying) "
      };

      string GetDescription(Boogie.ProofObligationDescription? description) {
        switch (assertionNode?.StatusVerification) {
          case GutterVerificationStatus.Verified: {
              var successDescription = description?.SuccessDescription ?? "_no message_";
              return $"{obsolescence}<span style='color:green'>**Success:**</span> " +
                     successDescription;
            }
          case GutterVerificationStatus.Error:
            var failureDescription = description?.FailureDescription ?? "_no message_";
            if (description is EnsuresDescription ensuresDescription) {
              if (currentlyHoveringPostcondition) {
                failureDescription = ensuresDescription.FailureDescriptionSingle;
              } else {
                failureDescription = ensuresDescription.FailureAtPathDescription;
              }
            }
            return $"{obsolescence}[**Error:**](https://dafny-lang.github.io/dafny/DafnyRef/DafnyRef#sec-verification-debugging) " +
                   failureDescription;
          case GutterVerificationStatus.Inconclusive:
            return $"{obsolescence}**Ignored or could not reach conclusion**";
          default: return $"{obsolescence}**Waiting to be verified...**";
        };
      }

      var counterexample = assertionNode.GetCounterExample();

      string information = "";

      string couldProveOrNotPrefix = (assertionNode.StatusVerification) switch {
        GutterVerificationStatus.Verified => "Did prove: ",
        GutterVerificationStatus.Error => "Could not prove: ",
        GutterVerificationStatus.Inconclusive => "Not able to prove: ",
        _ => "Unknown: "
      };

      string MoreInformation(Boogie.IToken? token, bool hoveringPostcondition) {
        string deltaInformation = "";
        while (token != null) {
          var errorToken = token;
          if (token is NestedToken nestedToken) {
            errorToken = nestedToken.Outer;
            token = nestedToken.Inner;
          } else {
            token = null;
          }

          // It's not necessary to restate the postcondition itself if the user is already hovering it
          // however, nested postconditions should be displayed
          if (errorToken is BoogieRangeToken rangeToken && !hoveringPostcondition) {
            var originalText = rangeToken.PrintOriginal();
            deltaInformation += "  \n" + (token == null ? couldProveOrNotPrefix : "Inside ") + "`" + originalText + "`";
          }

          hoveringPostcondition = false;
        }

        return deltaInformation;
      }

      if (counterexample is ReturnCounterexample returnCounterexample) {
        if (assertionNode.StatusVerification == GutterVerificationStatus.Error &&
            returnCounterexample.FailingEnsures.Description.SuccessDescription != "assertion always holds") {
          // Specialization for ensures marked with {:error} attribute
          // Note that GetDescription checks if user is hovering postcondition
          // so if there is no {:error}, it falls back to a correct error message
          information += GetDescription(returnCounterexample.FailingEnsures.Description);
        } else {
          information += GetDescription(returnCounterexample.FailingReturn.Description);
        }
        information += MoreInformation(returnCounterexample.FailingAssert.tok, currentlyHoveringPostcondition);
      } else if (counterexample is CallCounterexample callCounterexample) {
        if (assertionNode.StatusVerification == GutterVerificationStatus.Error &&
            callCounterexample.FailingRequires.Description.SuccessDescription != "assertion always holds"
            ) {
          // Specialization for requires marked with {:error} attribute
          information += GetDescription(callCounterexample.FailingRequires.Description);
        } else {
          information += GetDescription(callCounterexample.FailingCall.Description);
        }
        information += MoreInformation(callCounterexample.FailingRequires.tok, false);
      } else if (assertCmd is AssertRequiresCmd assertRequiresCmd) {
        information += GetDescription(assertRequiresCmd.Description);
        information += MoreInformation(assertRequiresCmd.Requires.tok, currentlyHoveringPostcondition);
      } else if (assertCmd is AssertEnsuresCmd assertEnsuresCmd) {
        information += GetDescription(assertEnsuresCmd.Description);
        information += MoreInformation(assertEnsuresCmd.Ensures.tok, currentlyHoveringPostcondition);
      } else {
        information += GetDescription(assertCmd?.Description);
        if (assertCmd?.tok is NestedToken) {
          information += MoreInformation(assertCmd.tok, currentlyHoveringPostcondition);
        }
      }

      information += "  \n";
      information += "This is " + (assertionCount == 1
        ? "the only assertion"
        : $"assertion #{assertionIndex + 1} of {assertionCount}");
      if (assertionBatchCount > 1) {
        information += $" in {batchRef} #{assertionBatch.RelativeNumber} of {assertionBatchCount}";
      }

      information += " in " + node.PrefixedDisplayName + "  \n";
      if (assertionBatchCount > 1) {
        information += AddAssertionBatchDocumentation("Batch") +
                       $" #{assertionBatch.RelativeNumber} resource usage: " +
                       FormatResourceCount(assertionBatch.ResourceCount);
      } else {
        information += "Resource usage: " +
                       FormatResourceCount(assertionBatch.ResourceCount);
      }

      // Not the main error displayed in diagnostics
      if (currentlyHoveringPostcondition) {
        information += "  \n" + (assertionNode?.SecondaryPosition != null
          ? $"Return path: {Path.GetFileName(assertionNode.Filename)}({assertionNode.SecondaryPosition.Line + 1}, {assertionNode.SecondaryPosition.Character + 1})"
          : "");
      }

      if (assertionNode?.GetCounterExample() is CallCounterexample) {
        information += "  \n" + (assertionNode.SecondaryPosition != null
          ? $"Failing precondition: {Path.GetFileName(assertionNode.Filename)}({assertionNode.SecondaryPosition.Line + 1}, {assertionNode.SecondaryPosition.Character + 1})"
          : "");
      }

      return information;
    }

    private static readonly int SignificativeDigits = 3;

    public static long RoundToSignificativeDigits(long nodeResourceCount) {
      var nDigits = ("" + nodeResourceCount).Length;
      if (nDigits > SignificativeDigits) {
        var toRemove = (long)Math.Pow(10, nDigits - SignificativeDigits);
        nodeResourceCount += toRemove / 2;
        nodeResourceCount -= (nodeResourceCount % toRemove);
      }

      return nodeResourceCount;
    }

    public static string FormatResourceCount(long nodeResourceCount) {
      var suffix = 0;
      var fractional = 0;
      nodeResourceCount = RoundToSignificativeDigits(nodeResourceCount);

      while (nodeResourceCount / 1000 >= 1 && suffix < 4) {
        fractional = (int)(nodeResourceCount % 1000);
        nodeResourceCount /= 1000;
        suffix += 1; // We don't go past 4 because no suffix beyond T should be useful
      }
      var nfi = new NumberFormatInfo() {
        NumberDecimalDigits = 0,
        NumberGroupSeparator = "",
      };
      var nodeResourceCountStr = nodeResourceCount.ToString(nfi);
      var fractionalStr = nodeResourceCountStr.Length >= SignificativeDigits || suffix == 0 ?
        "" :
        "." + $"{fractional:000}".Remove(SignificativeDigits - nodeResourceCountStr.Length);
      var letterSuffix = suffix switch {
        0 => "",
        1 => "K",
        2 => "M",
        3 => "G",
        _ => "T"
      };
      return $"{nodeResourceCountStr}{fractionalStr}{letterSuffix} RU";
    }

    private static string AddAssertionBatchDocumentation(string batchReference) {
      return $"[{batchReference}](https://dafny-lang.github.io/dafny/DafnyRef/DafnyRef#sec-verification-attributes-on-assert-statements)";
    }

    private static Hover CreateMarkdownHover(string information) {
      return new Hover {
        Contents = new MarkedStringsOrMarkupContent(
          new MarkupContent {
            Kind = MarkupKind.Markdown,
            Value = information
          }
        )
      };
    }

    private string CreateSymbolMarkdown(ILocalizableSymbol symbol, CancellationToken cancellationToken) {
      var docString = symbol.Node is IHasDocstring nodeWithDocstring ? nodeWithDocstring.GetDocstring(options) : "";
      return (docString + $"\n```dafny\n{symbol.GetDetailText(options, cancellationToken)}\n```").TrimStart();
    }
  }
}
