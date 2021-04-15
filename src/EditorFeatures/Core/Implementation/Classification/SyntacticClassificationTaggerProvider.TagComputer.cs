﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Tagging;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.Classification
{
    internal partial class SyntacticClassificationTaggerProvider
    {
        /// <summary>
        /// A classifier that operates only on the syntax of the source and not the semantics.  Note:
        /// this class operates in a hybrid sync/async manner.  Specifically, while classification
        /// happens synchronously, it may be synchronous over a parse tree which is out of date.  Then,
        /// asynchronously, we will attempt to get an up to date parse tree for the file. When we do, we
        /// will determine which sections of the file changed and we will use that to notify the editor
        /// about what needs to be reclassified.
        /// </summary>
        internal partial class TagComputer : ForegroundThreadAffinitizedObject
        {
            private readonly SyntacticClassificationTaggerProvider _taggerProvider;
            private readonly ITextBuffer _subjectBuffer;
            private readonly IAsynchronousOperationListener _listener;
            private readonly ClassificationTypeMap _typeMap;

            private readonly WorkspaceRegistration _workspaceRegistration;

            private readonly CancellationTokenSource _disposalCancellationSource = new();

            /// <summary>
            /// Work queue we use to batch up notifications about changes that will cause
            /// us to classify.  This ensures that if we hear a flurry of changes, we don't
            /// kick off an excessive amount of background work to process them.
            /// </summary>
            private readonly AsyncBatchingWorkQueue<ITextSnapshot> _workQueue;

            /// <summary>
            /// Timeout before we cancel the work to diff and return whatever we have.
            /// </summary>
            private readonly TimeSpan _diffTimeout;

            private Workspace? _workspace;

            // The latest data about the document being classified that we've cached.  objects can 
            // be accessed from both threads, and must be obtained when this lock is held. 
            //
            // _lastProcessedRoot is optional and is held onto for languages that support syntax.
            // it allows computing the root, and changed-spans, in the background so that we can
            // report a smaller change range, and have the data ready for classifying with GetTags
            // get called.

            private readonly object _gate = new();
            private ITextSnapshot? _lastProcessedSnapshot;
            private Document? _lastProcessedDocument;
            private SyntaxNode? _lastProcessedRoot;

            // this will cache previous classification information for a span, so that we can avoid
            // digging into same tree again and again to find exactly same answer
            private readonly LastLineCache _lastLineCache;

            private int _taggerReferenceCount;

            public TagComputer(
                SyntacticClassificationTaggerProvider taggerProvider,
                ITextBuffer subjectBuffer,
                IAsynchronousOperationListener asyncListener,
                ClassificationTypeMap typeMap,
                TimeSpan diffTimeout)
                : base(taggerProvider.ThreadingContext, assertIsForeground: false)
            {
                _taggerProvider = taggerProvider;
                _subjectBuffer = subjectBuffer;
                _listener = asyncListener;
                _typeMap = typeMap;
                _diffTimeout = diffTimeout;
                _workQueue = new AsyncBatchingWorkQueue<ITextSnapshot>(
                    TimeSpan.FromMilliseconds(TaggerConstants.NearImmediateDelay),
                    ProcessChangesAsync,
                    equalityComparer: null,
                    asyncListener,
                    _disposalCancellationSource.Token);

                _lastLineCache = new LastLineCache(taggerProvider.ThreadingContext);

                _workspaceRegistration = Workspace.GetWorkspaceRegistration(subjectBuffer.AsTextContainer());
                _workspaceRegistration.WorkspaceChanged += OnWorkspaceRegistrationChanged;

                if (_workspaceRegistration.Workspace != null)
                    ConnectToWorkspace(_workspaceRegistration.Workspace);

                // Prefer to hear about text changes in the BG so we can use as little 
                if (_subjectBuffer is ITextBuffer2 textBuffer2)
                    textBuffer2.ChangedOnBackground += this.OnSubjectBufferChanged;
                else
                    _subjectBuffer.Changed += this.OnSubjectBufferChanged;
            }

            public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

            private IClassificationService? TryGetClassificationService(ITextSnapshot snapshot)
                => _workspace?.Services.GetLanguageServices(snapshot.ContentType)?.GetService<IClassificationService>();

            #region Workspace Hookup

            private void OnWorkspaceRegistrationChanged(object? sender, EventArgs e)
            {
                var token = _listener.BeginAsyncOperation(nameof(OnWorkspaceRegistrationChanged));
                var task = SwitchToMainThreadAndHookupWorkspaceAsync();
                task.CompletesAsyncOperation(token);
            }

            private async Task SwitchToMainThreadAndHookupWorkspaceAsync()
            {
                try
                {
                    await this.ThreadingContext.JoinableTaskFactory.SwitchToMainThreadAsync(_disposalCancellationSource.Token);

                    // We both try to connect synchronously, and register for workspace registration events.
                    // It's possible (particularly in tests), to connect in the startup path, but then get a
                    // previously scheduled, but not yet delivered event.  Don't bother connecting to the
                    // same workspace again in that case.
                    var newWorkspace = _workspaceRegistration.Workspace;
                    if (newWorkspace == _workspace)
                        return;

                    DisconnectFromWorkspace();

                    if (newWorkspace != null)
                        ConnectToWorkspace(newWorkspace);
                }
                catch (OperationCanceledException)
                {
                    // can happen if we were disposed of.
                }
                catch (Exception e) when (FatalError.ReportAndCatch(e))
                {
                    // We were in a fire and forget task.  So just report the NFW and do not let
                    // this bleed out to an unhandled exception.
                }
            }

            internal void IncrementReferenceCount()
            {
                this.AssertIsForeground();
                _taggerReferenceCount++;
            }

            internal void DecrementReferenceCount()
            {
                this.AssertIsForeground();
                _taggerReferenceCount--;

                if (_taggerReferenceCount == 0)
                {
                    // stop any bg work we're doing.
                    _disposalCancellationSource.Cancel();

                    if (_subjectBuffer is ITextBuffer2 textBuffer2)
                        textBuffer2.ChangedOnBackground -= this.OnSubjectBufferChanged;
                    else
                        _subjectBuffer.Changed -= this.OnSubjectBufferChanged;

                    DisconnectFromWorkspace();
                    _workspaceRegistration.WorkspaceChanged -= OnWorkspaceRegistrationChanged;
                    _taggerProvider.DisconnectTagComputer(_subjectBuffer);
                }
            }

            private void ConnectToWorkspace(Workspace workspace)
            {
                this.AssertIsForeground();

                _workspace = workspace;
                _workspace.WorkspaceChanged += this.OnWorkspaceChanged;
                _workspace.DocumentOpened += this.OnDocumentOpened;
                _workspace.DocumentActiveContextChanged += this.OnDocumentActiveContextChanged;

                // Now that we've connected to the workspace, kick off work to reclassify this buffer.
                _workQueue.AddWork(_subjectBuffer.CurrentSnapshot);
            }

            public void DisconnectFromWorkspace()
            {
                this.AssertIsForeground();

                lock (_gate)
                {
                    _lastProcessedDocument = null;
                    _lastProcessedSnapshot = null;
                    _lastProcessedRoot = null;
                }

                if (_workspace != null)
                {
                    _workspace.WorkspaceChanged -= this.OnWorkspaceChanged;
                    _workspace.DocumentOpened -= this.OnDocumentOpened;
                    _workspace.DocumentActiveContextChanged -= this.OnDocumentActiveContextChanged;

                    _workspace = null;

                    // Now that we've disconnected to the workspace, kick off work to reclassify this buffer.
                    _workQueue.AddWork(_subjectBuffer.CurrentSnapshot);
                }
            }

            #endregion

            #region Event Handling

            private void OnSubjectBufferChanged(object? sender, TextContentChangedEventArgs args)
            {
                // we know a change to our buffer is always affecting this document.  So we can
                // just enqueue the work to reclassify things unilaterally.
                _workQueue.AddWork(args.After);
            }

            private void OnDocumentActiveContextChanged(object? sender, DocumentActiveContextChangedEventArgs args)
            {
                EnqueueWorkIfThisDocument(args.NewActiveContextDocumentId);
            }

            private void OnDocumentOpened(object? sender, DocumentEventArgs args)
            {
                EnqueueWorkIfThisDocument(args.Document.Id);
            }

            private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs args)
            {
                // We may be getting an event for a workspace we already disconnected from.  If so,
                // ignore them.  We won't be able to find the Document corresponding to our text buffer,
                // so we can't reasonably classify this anyways.
                if (args.NewSolution.Workspace != _workspace)
                    return;

                if (args.Kind != WorkspaceChangeKind.ProjectChanged)
                    return;

                var documentId = _workspace.GetDocumentIdInCurrentContext(_subjectBuffer.AsTextContainer());
                if (args.ProjectId != documentId?.ProjectId)
                    return;

                var oldProject = args.OldSolution.GetProject(args.ProjectId);
                var newProject = args.NewSolution.GetProject(args.ProjectId);

                // make sure in case of parse config change, we re-colorize whole document. not just edited section.
                if (Equals(oldProject?.ParseOptions, newProject?.ParseOptions))
                    return;

                _workQueue.AddWork(_subjectBuffer.CurrentSnapshot);
            }

            private void EnqueueWorkIfThisDocument(DocumentId documentId)
            {
                if (_workspace == null)
                    return;

                var bufferDocumentId = _workspace.GetDocumentIdInCurrentContext(_subjectBuffer.AsTextContainer());
                if (bufferDocumentId != documentId)
                    return;

                _workQueue.AddWork(_subjectBuffer.CurrentSnapshot);
            }

            #endregion

            /// <summary>
            /// Parses the document in the background and determines what has changed to report to
            /// the editor.  Calls to <see cref="ProcessChangesAsync"/> are serialized by <see cref="AsyncBatchingWorkQueue{TItem}"/>
            /// so we don't need to worry about multiple calls to this happening concurrently.
            /// </summary>
            private async Task ProcessChangesAsync(ImmutableArray<ITextSnapshot> snapshots, CancellationToken cancellationToken)
            {
                // We have potentially heard about several changes to the subject buffer.  However
                // we only need to process the latest once.
                Contract.ThrowIfTrue(snapshots.IsDefaultOrEmpty);
                var currentSnapshot = GetLatest(snapshots);

                var classificationService = TryGetClassificationService(currentSnapshot);
                if (classificationService == null)
                    return;

                var currentDocument = currentSnapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (currentDocument == null)
                    return;

                GetLastProcessedData(out var previousSnapshot, out var previousDocument, out var previousRoot);

                // Optionally pre-calculate the root of the doc so that it is ready to classify
                // once GetTags is called.  Also, attempt to determine a smallwe change range span
                // for this document so that we can avoid reporting the entire document as changed.

                var currentRoot = await currentDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var changedSpan = await ComputeChangedSpanAsync().ConfigureAwait(false);

                lock (_gate)
                {
                    _lastProcessedSnapshot = currentSnapshot;
                    _lastProcessedDocument = currentDocument;
                    _lastProcessedRoot = currentRoot;
                }

                // Notify the editor now that there were changes.  Note: we do not need to go the
                // UI to do this.  The editor supports TagsChanged being called on any thread.
                this.TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(changedSpan));

                return;

                static ITextSnapshot GetLatest(ImmutableArray<ITextSnapshot> snapshots)
                {
                    var latest = snapshots[0];
                    for (var i = 1; i < snapshots.Length; i++)
                    {
                        var snapshot = snapshots[i];
                        if (snapshot.Version.VersionNumber > latest.Version.VersionNumber)
                            latest = snapshot;
                    }

                    return latest;
                }

                async ValueTask<SnapshotSpan> ComputeChangedSpanAsync()
                {
                    var changeRange = await ComputeChangedRangeAsync().ConfigureAwait(false);
                    return changeRange == null
                        ? currentSnapshot.GetFullSpan()
                        : currentSnapshot.GetSpan(changeRange.Value.Span.Start, changeRange.Value.NewLength);
                }

                ValueTask<TextChangeRange?> ComputeChangedRangeAsync()
                {
                    // If we have syntax available fast path the change computation without async or blocking.
                    if (previousRoot != null && currentRoot != null)
                        return new(classificationService.ComputeSyntacticChangeRange(currentDocument.Project.Solution.Workspace, previousRoot, currentRoot, _diffTimeout, cancellationToken));

                    // Otherwise, fall back to the language to compute the difference based on the document contents.
                    if (previousDocument != null)
                        return classificationService.ComputeSyntacticChangeRangeAsync(previousDocument, currentDocument, _diffTimeout, cancellationToken);

                    return new ValueTask<TextChangeRange?>();
                }
            }

            private void GetLastProcessedData(out ITextSnapshot? previousSnapshot, out Document? previousDocument, out SyntaxNode? previousRoot)
            {
                lock (_gate)
                {
                    previousSnapshot = _lastProcessedSnapshot;
                    previousDocument = _lastProcessedDocument;
                    previousRoot = _lastProcessedRoot;
                }
            }

            public IEnumerable<ITagSpan<IClassificationTag>> GetTags(NormalizedSnapshotSpanCollection spans)
            {
                this.AssertIsForeground();

                using (Logger.LogBlock(FunctionId.Tagger_SyntacticClassification_TagComputer_GetTags, CancellationToken.None))
                {
                    return GetTagsWorker(spans) ?? SpecializedCollections.EmptyEnumerable<ITagSpan<IClassificationTag>>();
                }
            }

            private IEnumerable<ITagSpan<IClassificationTag>>? GetTagsWorker(NormalizedSnapshotSpanCollection spans)
            {
                this.AssertIsForeground();
                if (spans.Count == 0 || _workspace == null)
                    return null;

                var cancellationToken = CancellationToken.None;
                var snapshot = spans[0].Snapshot;

                var classificationService = TryGetClassificationService(snapshot);
                if (classificationService == null)
                    return null;

                var classifiedSpans = ClassificationUtilities.GetOrCreateClassifiedSpanList();

                foreach (var span in spans)
                    AddClassifications(span);

                return ClassificationUtilities.ConvertAndReturnList(_typeMap, snapshot, classifiedSpans);

                void AddClassifications(SnapshotSpan span)
                {
                    this.AssertIsForeground();

                    // First, get the tree and snapshot that we'll be operating over.  
                    GetLastProcessedData(out var lastProcessedSnapshot, out var lastProcessedDocument, out var lastProcessedRoot);

                    if (lastProcessedDocument == null)
                    {
                        // We don't have a syntax tree yet.  Just do a lexical classification of the document.
                        AddLexicalClassifications(span);
                    }
                    else
                    {
                        // If we have a document, we must have a snapshot as well.
                        Contract.ThrowIfNull(lastProcessedSnapshot);

                        // We have a tree.  However, the tree may be for an older version of the snapshot.
                        // If it is for an older version, then classify that older version and translate
                        // the classifications forward.  Otherwise, just classify normally.

                        if (lastProcessedSnapshot.Version.ReiteratedVersionNumber == span.Snapshot.Version.ReiteratedVersionNumber)
                        {
                            AddSyntacticClassificationsForDocument(span, lastProcessedDocument, lastProcessedRoot);
                        }
                        else
                        {
                            // Slightly more complicated.  We have a parse tree, it's just not for the snapshot
                            // we're being asked for.
                            AddClassifiedSpansForPreviousDocument(span, lastProcessedSnapshot, lastProcessedDocument, lastProcessedRoot);
                        }
                    }
                }

                void AddLexicalClassifications(SnapshotSpan span)
                {
                    this.AssertIsForeground();

                    classificationService.AddLexicalClassifications(
                        span.Snapshot.AsText(), span.Span.ToTextSpan(), classifiedSpans, CancellationToken.None);
                }

                void AddSyntacticClassificationsForDocument(SnapshotSpan span, Document document, SyntaxNode? root)
                {
                    this.AssertIsForeground();

                    if (!_lastLineCache.TryUseCache(span, out var tempList))
                    {
                        tempList = ClassificationUtilities.GetOrCreateClassifiedSpanList();

                        if (root != null)
                        {
                            classificationService.AddSyntacticClassifications(document.Project.Solution.Workspace, root, span.Span.ToTextSpan(), tempList, cancellationToken);
                        }
                        else
                        {
                            classificationService.AddSyntacticClassificationsAsync(document, span.Span.ToTextSpan(), tempList, cancellationToken).Wait(cancellationToken);
                        }

                        _lastLineCache.Update(span, tempList);
                    }

                    // simple case.  They're asking for the classifications for a tree that we already have.
                    // Just get the results from the tree and return them.

                    classifiedSpans.AddRange(tempList);
                }

                void AddClassifiedSpansForPreviousDocument(
                    SnapshotSpan span, ITextSnapshot lastProcessedSnapshot, Document lastProcessedDocument, SyntaxNode? lastProcessedRoot)
                {
                    this.AssertIsForeground();

                    // Slightly more complicated case.  They're asking for the classifications for a
                    // different snapshot than what we have a parse tree for.  So we first translate the span
                    // that they're asking for so that is maps onto the tree that we have spans for.  We then
                    // get the classifications from that tree.  We then take the results and translate them
                    // back to the snapshot they want.  Finally, as some of the classifications may have
                    // changed, we check for some common cases and touch them up manually so that things
                    // look right for the user.

                    // Note the handling of SpanTrackingModes here: We do EdgeExclusive while mapping back ,
                    // and EdgeInclusive mapping forward. What I've convinced myself is that EdgeExclusive
                    // is best when mapping back over a deletion, so that we don't end up classifying all of
                    // the deleted code.  In most addition/modification cases, there will be overlap with
                    // existing spans, and so we'll end up classifying well.  In the worst case, there is a
                    // large addition that doesn't exist when we map back, and so we don't have any
                    // classifications for it. That's probably okay, because: 

                    // 1. If it's that large, it's likely that in reality there are multiple classification
                    // spans within it.

                    // 2.We'll eventually call ClassificationsChanged and re-classify that region anyway.

                    // When mapping back forward, we use EdgeInclusive so that in the common typing cases we
                    // don't end up with half a token colored differently than the other half.

                    // See bugs like http://vstfdevdiv:8080/web/wi.aspx?id=6176 for an example of what can
                    // happen when this goes wrong.

                    // 1) translate the requested span onto the right span for the snapshot that corresponds
                    //    to the syntax tree.
                    var translatedSpan = span.TranslateTo(lastProcessedSnapshot, SpanTrackingMode.EdgeExclusive);
                    if (translatedSpan.IsEmpty)
                    {
                        // well, there is no information we can get from previous tree, use lexer to
                        // classify given span. soon we will re-classify the region.
                        AddLexicalClassifications(span);
                    }
                    else
                    {

                        var tempList = ClassificationUtilities.GetOrCreateClassifiedSpanList();
                        AddSyntacticClassificationsForDocument(span, lastProcessedDocument, lastProcessedRoot);

                        var currentSnapshot = span.Snapshot;
                        var currentText = currentSnapshot.AsText();
                        foreach (var lastClassifiedSpan in tempList)
                        {
                            // 2) Translate those classifications forward so that they correspond to the true
                            //    requested snapshot.
                            var lastSnapshotSpan = lastClassifiedSpan.TextSpan.ToSnapshotSpan(lastProcessedSnapshot);
                            var currentSnapshotSpan = lastSnapshotSpan.TranslateTo(currentSnapshot, SpanTrackingMode.EdgeInclusive);

                            var currentClassifiedSpan = new ClassifiedSpan(lastClassifiedSpan.ClassificationType, currentSnapshotSpan.Span.ToTextSpan());

                            // 3) The classifications may be incorrect due to changes in the text.  For example,
                            //    if "clss" becomes "class", then we want to changes the classification from
                            //    'identifier' to 'keyword'.
                            currentClassifiedSpan = classificationService.AdjustStaleClassification(currentText, currentClassifiedSpan);

                            classifiedSpans.Add(currentClassifiedSpan);
                        }

                        ClassificationUtilities.ReturnClassifiedSpanList(tempList);
                    }
                }
            }
        }
    }
}
