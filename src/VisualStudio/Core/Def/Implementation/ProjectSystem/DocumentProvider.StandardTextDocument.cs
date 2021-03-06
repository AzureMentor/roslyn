﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem
{
    internal partial class DocumentProvider
    {
        [DebuggerDisplay("{GetDebuggerDisplay(),nq}")]
        private class StandardTextDocument : ForegroundThreadAffinitizedObject, IVisualStudioHostDocument
        {
            /// <summary>
            /// The IDocumentProvider that created us.
            /// </summary>
            private readonly DocumentProvider _documentProvider;
            private readonly string _itemMoniker;
            private readonly FileChangeTracker _fileChangeTracker;
            private readonly ReiteratedVersionSnapshotTracker _snapshotTracker;
            private readonly TextLoader _doNotAccessDirectlyLoader;

            /// <summary>
            /// The text buffer that is open in the editor. When the file is closed, this is null.
            /// </summary>
            private ITextBuffer _openTextBuffer;

            public DocumentId Id { get; }
            public IReadOnlyList<string> Folders { get; }
            public AbstractProject Project { get; }
            public SourceCodeKind SourceCodeKind { get; }
            public DocumentKey Key { get; }

            public event EventHandler UpdatedOnDisk;
            public event EventHandler<bool> Opened;
            public event EventHandler<bool> Closing;

            /// <summary>
            /// Creates a <see cref="StandardTextDocument"/>.
            /// </summary>
            public StandardTextDocument(
                DocumentProvider documentProvider,
                AbstractProject project,
                DocumentKey documentKey,
                ImmutableArray<string> folderNames,
                SourceCodeKind sourceCodeKind,
                IVsFileChangeEx fileChangeService,
                ITextBuffer openTextBuffer,
                DocumentId id,
                EventHandler updatedOnDiskHandler,
                EventHandler<bool> openedHandler,
                EventHandler<bool> closingHandler)
                : base(documentProvider.ThreadingContext)
            {
                Contract.ThrowIfNull(documentProvider);

                this.Project = project;
                this.Id = id ?? DocumentId.CreateNewId(project.Id, documentKey.Moniker);
                _itemMoniker = documentKey.Moniker;

                this.Folders = folderNames;

                _documentProvider = documentProvider;

                this.Key = documentKey;
                this.SourceCodeKind = sourceCodeKind;
                _fileChangeTracker = new FileChangeTracker(fileChangeService, this.FilePath);
                _fileChangeTracker.UpdatedOnDisk += OnUpdatedOnDisk;

                _openTextBuffer = openTextBuffer;
                _snapshotTracker = new ReiteratedVersionSnapshotTracker(openTextBuffer);

                // The project system does not tell us the CodePage specified in the proj file, so
                // we use null to auto-detect.
                _doNotAccessDirectlyLoader = new FileChangeTrackingTextLoader(_fileChangeTracker, new FileTextLoader(documentKey.Moniker, defaultEncoding: null));

                // If we aren't already open in the editor, then we should create a file change notification
                if (openTextBuffer == null)
                {
                    _fileChangeTracker.StartFileChangeListeningAsync();
                }

                if (updatedOnDiskHandler != null)
                {
                    UpdatedOnDisk += updatedOnDiskHandler;
                }

                if (openedHandler != null)
                {
                    Opened += openedHandler;
                }

                if (closingHandler != null)
                {
                    Closing += closingHandler;
                }
            }

            public bool IsOpen
            {
                get { return _openTextBuffer != null; }
            }

            public string FilePath
            {
                get { return Key.Moniker; }
            }

            public string Name
            {
                get
                {
                    try
                    {
                        return Path.GetFileName(this.FilePath);
                    }
                    catch (ArgumentException)
                    {
                        return this.FilePath;
                    }
                }
            }

            public TextLoader Loader
            {
                get
                {
                    return _doNotAccessDirectlyLoader;
                }
            }

            public DocumentInfo GetInitialState()
            {
                return DocumentInfo.Create(
                    id: this.Id,
                    name: this.Name,
                    folders: this.Folders,
                    sourceCodeKind: this.SourceCodeKind,
                    loader: this.Loader,
                    filePath: this.FilePath);
            }

            internal void ProcessOpen(ITextBuffer openedBuffer, bool isCurrentContext)
            {
                Debug.Assert(openedBuffer != null);

                _fileChangeTracker.StopFileChangeListening();
                _snapshotTracker.StartTracking(openedBuffer);

                _openTextBuffer = openedBuffer;
                Opened?.Invoke(this, isCurrentContext);
            }

            internal void ProcessClose(bool updateActiveContext)
            {
                // Todo: it might already be closed...
                // For now, continue asserting as it can be clicked through.
                Debug.Assert(_openTextBuffer != null);
                Closing?.Invoke(this, updateActiveContext);

                var buffer = _openTextBuffer;
                _openTextBuffer = null;

                _snapshotTracker.StopTracking(buffer);
                _fileChangeTracker.StartFileChangeListeningAsync();
            }

            public SourceTextContainer GetOpenTextContainer()
            {
                return _openTextBuffer.AsTextContainer();
            }

            public ITextBuffer GetOpenTextBuffer()
            {
                return _openTextBuffer;
            }

            private void OnUpdatedOnDisk(object sender, EventArgs e)
            {
                UpdatedOnDisk?.Invoke(this, EventArgs.Empty);
            }

            public void Dispose()
            {
                _fileChangeTracker.Dispose();
                _fileChangeTracker.UpdatedOnDisk -= OnUpdatedOnDisk;

                _documentProvider.StopTrackingDocument(this);
            }

            public void UpdateText(SourceText newText)
            {
                // Avoid opening the invisible editor if we already have a buffer.  It takes a relatively
                // expensive source control check if the file is already checked out.
                if (_openTextBuffer != null)
                {
                    TextEditApplication.UpdateText(newText, _openTextBuffer, EditOptions.DefaultMinimalChange);
                }
                else
                {
                    using (var invisibleEditor = ((VisualStudioWorkspaceImpl)this.Project.Workspace).OpenInvisibleEditor(this))
                    {
                        TextEditApplication.UpdateText(newText, invisibleEditor.TextBuffer, EditOptions.None);
                    }
                }
            }

            public ITextBuffer GetTextUndoHistoryBuffer()
            {
                return GetOpenTextBuffer();
            }

            private string GetDebuggerDisplay()
            {
                return this.Name;
            }

            public uint GetItemId()
            {
                AssertIsForeground();

                if (_itemMoniker == null || Project.Hierarchy == null)
                {
                    return (uint)VSConstants.VSITEMID.Nil;
                }

                return Project.Hierarchy.TryGetItemId(_itemMoniker);
            }

            /// <summary>
            /// A wrapper for a <see cref="TextLoader"/> that ensures we are watching file contents prior to reading the file.
            /// </summary>
            private sealed class FileChangeTrackingTextLoader : TextLoader
            {
                private readonly FileChangeTracker _fileChangeTracker;
                private readonly TextLoader _innerTextLoader;

                public FileChangeTrackingTextLoader(FileChangeTracker fileChangeTracker, TextLoader innerTextLoader)
                {
                    _fileChangeTracker = fileChangeTracker;
                    _innerTextLoader = innerTextLoader;
                }

                public override Task<TextAndVersion> LoadTextAndVersionAsync(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken)
                {
                    _fileChangeTracker.EnsureSubscription();
                    return _innerTextLoader.LoadTextAndVersionAsync(workspace, documentId, cancellationToken);
                }

                internal override TextAndVersion LoadTextAndVersionSynchronously(Workspace workspace, DocumentId documentId, CancellationToken cancellationToken)
                {
                    _fileChangeTracker.EnsureSubscription();
                    return _innerTextLoader.LoadTextAndVersionSynchronously(workspace, documentId, cancellationToken);
                }
            }
        }
    }
}
