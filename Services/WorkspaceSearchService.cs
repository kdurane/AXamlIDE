using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AXamlIDE.Models;

namespace AXamlIDE.Services
{
    public sealed class WorkspaceSearchService
    {
        private const long MaximumFileSize = 2 * 1024 * 1024;
        private const int MaximumResults = 500;
        private const int MaximumReplaceMatches = 5000;
        private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
              {
                  ".git", ".idea", ".vs", "bin", "obj", "node_modules", "packages"
              };

        public Task<WorkspaceSearchSummary> SearchAsync(
            string rootPath,
            string query,
            IReadOnlyDictionary<string, string> openDocumentTexts,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(query);

            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Workspace directory '{root}' does not exist.");

            return Task.Run(() => Search(root, query, openDocumentTexts, cancellationToken), cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListWorkspaceFilesAsync(
            string rootPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Workspace directory '{root}' does not exist.");

            return Task.Run<IReadOnlyList<string>>(() =>
            {
                var files = new List<string>();
                var directories = new Stack<string>();
                directories.Push(root);
                while (directories.TryPop(out var directory) && files.Count < 20000)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        files.AddRange(Directory.EnumerateFiles(directory).Take(20000 - files.Count));
                        foreach (var child in Directory.EnumerateDirectories(directory)
                                     .Where(IsSearchableDirectory))
                            directories.Push(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }
                }

                return files;
            }, cancellationToken);
        }

        public Task<WorkspaceReplacePlan> PrepareReplaceAsync(
            string rootPath,
            string query,
            string replacement,
            IReadOnlyDictionary<string, string> openDocumentTexts,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(query);
            ArgumentNullException.ThrowIfNull(replacement);

            var root = Path.GetFullPath(rootPath);
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"Workspace directory '{root}' does not exist.");

            return Task.Run(
                () => PrepareReplace(root, query, replacement, openDocumentTexts, cancellationToken),
                cancellationToken);
        }

        public WorkspaceReplaceSummary ApplyReplacePlan(
            WorkspaceReplacePlan plan,
            IReadOnlyDictionary<string, OpenDocument> openDocuments)
        {
            if (plan.LimitReached)
                throw new InvalidOperationException("The replace plan exceeded the safe match limit. Narrow the search and try again.");

            var replacedFiles = 0;
            var replacedMatches = 0;
            var failedFiles = 0;
            var modifiedOpenDocuments = 0;

            foreach (var change in plan.Files)
            {
                if (openDocuments.TryGetValue(Path.GetFullPath(change.FilePath), out var document))
                {
                    if (!string.Equals(document.Document.Text, change.OriginalText, StringComparison.Ordinal))
                    {
                        failedFiles++;
                        continue;
                    }

                    document.Document.Text = change.ReplacedText;
                    modifiedOpenDocuments++;
                    replacedFiles++;
                    replacedMatches += change.MatchCount;
                    continue;
                }

                try
                {
                    var currentText = ReadText(change.FilePath, out var encoding);
                    if (!string.Equals(currentText, change.OriginalText, StringComparison.Ordinal))
                    {
                        failedFiles++;
                        continue;
                    }

                    var tempPath = Path.Combine(
                        Path.GetDirectoryName(change.FilePath)!,
                        $".{Path.GetFileName(change.FilePath)}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        File.WriteAllText(tempPath, change.ReplacedText, encoding);
                        File.Move(tempPath, change.FilePath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }

                    replacedFiles++;
                    replacedMatches += change.MatchCount;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failedFiles++;
                }
            }

            return new WorkspaceReplaceSummary(replacedFiles, replacedMatches, failedFiles, modifiedOpenDocuments);
        }

        private static WorkspaceReplacePlan PrepareReplace(
            string root,
            string query,
            string replacement,
            IReadOnlyDictionary<string, string> openDocumentTexts,
            CancellationToken cancellationToken)
        {
            var openDocuments = openDocumentTexts.ToDictionary(
                pair => Path.GetFullPath(pair.Key),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var changes = new List<WorkspaceFileReplacement>();
            var directories = new Stack<string>();
            directories.Push(root);
            var matchCount = 0;

            while (directories.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<string> files;
                IEnumerable<string> childDirectories;
                try
                {
                    files = Directory.EnumerateFiles(directory).ToArray();
                    childDirectories = Directory.EnumerateDirectories(directory)
                        .Where(IsSearchableDirectory)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string originalText;
                    if (openDocuments.TryGetValue(Path.GetFullPath(file), out var openText))
                    {
                        originalText = openText;
                    }
                    else
                    {
                        try
                        {
                            if (new FileInfo(file).Length > MaximumFileSize)
                                continue;
                            originalText = ReadText(file, out _);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }
                    }

                    if (originalText.Contains('\0'))
                        continue;

                    var (replacedText, fileMatchCount) = ReplaceLiteral(originalText, query, replacement);
                    if (fileMatchCount == 0 || string.Equals(originalText, replacedText, StringComparison.Ordinal))
                        continue;

                    matchCount += fileMatchCount;
                    if (matchCount > MaximumReplaceMatches)
                        return new WorkspaceReplacePlan([], matchCount, LimitReached: true);
                    changes.Add(new WorkspaceFileReplacement(file, originalText, replacedText, fileMatchCount));
                }

                foreach (var childDirectory in childDirectories)
                    directories.Push(childDirectory);
            }

            return new WorkspaceReplacePlan(changes, matchCount, LimitReached: false);
        }

        private static (string Text, int MatchCount) ReplaceLiteral(string text, string query, string replacement)
        {
            var output = new StringBuilder(text.Length);
            var matchCount = 0;
            var copiedThrough = 0;
            var searchFrom = 0;

            while ((searchFrom = text.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                output.Append(text, copiedThrough, searchFrom - copiedThrough);
                output.Append(replacement);
                searchFrom += query.Length;
                copiedThrough = searchFrom;
                matchCount++;
            }

            if (matchCount == 0)
                return (text, 0);

            output.Append(text, copiedThrough, text.Length - copiedThrough);
            return (output.ToString(), matchCount);
        }

        private static string ReadText(string path, out Encoding encoding)
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            encoding = reader.CurrentEncoding;
            return text;
        }

        private static bool IsSearchableDirectory(string path)
        {
            if (ExcludedDirectoryNames.Contains(Path.GetFileName(path)))
                return false;

            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static WorkspaceSearchSummary Search(
            string root,
            string query,
            IReadOnlyDictionary<string, string> openDocumentTexts,
            CancellationToken cancellationToken)
        {
            var openDocuments = openDocumentTexts.ToDictionary(
                pair => Path.GetFullPath(pair.Key),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var results = new List<WorkspaceSearchResult>();
            var directories = new Stack<string>();
            directories.Push(root);
            var skippedLargeFiles = 0;
            var unreadableFiles = 0;
            var resultLimitReached = false;

            while (directories.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                IEnumerable<string> files;
                IEnumerable<string> childDirectories;
                try
                {
                    files = Directory.EnumerateFiles(directory).ToArray();
                    childDirectories = Directory.EnumerateDirectories(directory)
                        .Where(IsSearchableDirectory)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unreadableFiles++;
                    continue;
                }

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string contents;
                    if (openDocuments.TryGetValue(Path.GetFullPath(file), out var openText))
                    {
                        contents = openText;
                    }
                    else
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.Length > MaximumFileSize)
                            {
                                skippedLargeFiles++;
                                continue;
                            }

                            contents = File.ReadAllText(file);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            unreadableFiles++;
                            continue;
                        }
                    }

                    if (contents.Contains('\0'))
                        continue;

                    var lineNumber = 0;
                    using var reader = new StringReader(contents);
                    while (reader.ReadLine() is { } line)
                    {
                        lineNumber++;
                        var searchFrom = 0;
                        while ((searchFrom = line.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase)) >= 0)
                        {
                            results.Add(new WorkspaceSearchResult(file, lineNumber, searchFrom, line.Trim()));
                            if (results.Count >= MaximumResults)
                            {
                                resultLimitReached = true;
                                break;
                            }

                            searchFrom += query.Length;
                        }

                        if (resultLimitReached)
                            break;
                    }

                    if (resultLimitReached)
                        break;
                }

                if (resultLimitReached)
                    break;

                foreach (var childDirectory in childDirectories)
                    directories.Push(childDirectory);
            }

            return new WorkspaceSearchSummary(results, skippedLargeFiles, unreadableFiles, resultLimitReached);
        }
    }
}
