using System.Collections.Generic;

namespace AXamlIDE.Search;

public sealed record WorkspaceSearchResult(string FilePath, int LineNumber, int Column, string LineText);
public sealed record WorkspaceSearchSummary(IReadOnlyList<WorkspaceSearchResult> Results, int SkippedLargeFiles, int UnreadableFiles, bool ResultLimitReached);
public sealed record WorkspaceFileReplacement(string FilePath, string OriginalText, string ReplacedText, int MatchCount);
public sealed record WorkspaceReplacePlan(IReadOnlyList<WorkspaceFileReplacement> Files, int MatchCount, bool LimitReached);
public sealed record WorkspaceReplaceSummary(int ReplacedFiles, int ReplacedMatches, int FailedFiles, int ModifiedOpenDocuments);
