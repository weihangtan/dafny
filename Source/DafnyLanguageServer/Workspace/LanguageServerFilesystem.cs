using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using DafnyCore.Options;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Serilog.Core;

namespace Microsoft.Dafny.LanguageServer.Workspace;

public class LanguageServerFilesystem : IFileSystem {
  private readonly ILogger<LanguageServerFilesystem> logger;

  public LanguageServerFilesystem(ILogger<LanguageServerFilesystem> logger) {
    this.logger = logger;
  }

  private class Entry {
    public TextBuffer Buffer { get; set; }
    public int Version { get; set; }

    public Entry(TextBuffer buffer, int version) {
      Buffer = buffer;
      Version = version;
    }
  }

  private readonly ConcurrentDictionary<Uri, Entry> openFiles = new();

  public void OpenDocument(TextDocumentItem document) {
    var uri = document.Uri.ToUri();
    if (openFiles.ContainsKey(uri)) {
      throw new InvalidOperationException($"Cannot open file {uri} because it is already open");
    }

    openFiles[uri] = new Entry(new TextBuffer(document.Text), document.Version!.Value);
  }

  public void UpdateDocument(DidChangeTextDocumentParams documentChange) {
    var uri = documentChange.TextDocument.Uri.ToUri();
    if (!openFiles.TryGetValue(uri, out var entry)) {
      throw new InvalidOperationException("Cannot update file that has not been opened");
    }

    var buffer = entry.Buffer;
    var mergedBuffer = buffer;
    foreach (var change in documentChange.ContentChanges) {
      mergedBuffer = mergedBuffer.ApplyTextChange(change);
    }
    entry.Buffer = mergedBuffer;

    // According to the LSP specification, document versions should increase monotonically but may be non-consecutive.
    // See: https://github.com/microsoft/language-server-protocol/blob/gh-pages/_specifications/specification-3-16.md?plain=1#L1195
    var oldVer = entry.Version;
    var newVersion = documentChange.TextDocument.Version;
    var documentUri = documentChange.TextDocument.Uri;
    if (oldVer >= newVersion) {
      throw new InvalidOperationException(
        $"the updates of document {documentUri} are out-of-order: {oldVer} -> {newVersion}");
    }

    entry.Version = newVersion!.Value;

  }

  public void CloseDocument(TextDocumentIdentifier document) {
    var uri = document.Uri.ToUri();

    logger.LogInformation($"Closing document {document.Uri}");
    if (!openFiles.TryRemove(uri, out _)) {
      logger.LogError($"Could not close file {uri} because it was not open.");
    }
  }

  public TextReader ReadFile(Uri uri) {
    if (openFiles.TryGetValue(uri, out var entry)) {
      return new StringReader(entry.Buffer.Text);
    }

    return OnDiskFileSystem.Instance.ReadFile(uri);
  }

  public bool Exists(Uri path) {
    return openFiles.ContainsKey(path) || OnDiskFileSystem.Instance.Exists(path);
  }

  public DirectoryInfoBase GetDirectoryInfoBase(string root) {
    var inMemoryFiles = openFiles.Keys.Select(openFileUri => openFileUri.LocalPath);
    var inMemory = new InMemoryDirectoryInfoFromDotNet8(root, inMemoryFiles);

    return new CombinedDirectoryInfo(new[] { inMemory, OnDiskFileSystem.Instance.GetDirectoryInfoBase(root) });
  }

  /// <summary>
  /// Return the version of a particular file.
  /// When the client sends file updates, it includes a new version for this file, which we store and return here.
  /// File version are important to the client because it can use them to do client side migration of positions.
  /// </summary>
  public int? GetVersion(Uri uri) {
    if (openFiles.TryGetValue(uri, out var file)) {
      return file.Version;
    }

    return null;
  }
}