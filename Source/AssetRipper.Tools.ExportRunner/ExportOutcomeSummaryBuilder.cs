using AssetRipper.Export.PrimaryContent;

namespace AssetRipper.Tools.ExportRunner;

internal static class ExportOutcomeSummaryBuilder
{
	public static ExportRunOutcomeSummary BuildRunSummary(IReadOnlyList<ExportJobManifest> jobs)
	{
		return new ExportRunOutcomeSummary(
			JobCount: jobs.Count,
			SuccessCount: jobs.Count(job => string.Equals(job.Status, "success", StringComparison.Ordinal)),
			SkippedCount: jobs.Count(job => string.Equals(job.Status, "skipped", StringComparison.Ordinal)),
			FailedCount: jobs.Count(job => string.Equals(job.Status, "failed", StringComparison.Ordinal)),
			SelectedCollectionCount: jobs.Sum(job => job.Selection?.SelectedCollections ?? 0),
			SelectionSkippedCollectionCount: jobs.Sum(job => job.Selection?.SkippedCollections ?? 0),
			ExportFailedCollectionCount: jobs.Sum(job => job.Selection?.FailedCollections ?? 0),
			RecursiveUnpackCandidateCount: jobs.Sum(job => job.RecursiveUnpack?.CandidateFileCount ?? 0),
			RecursiveUnpackAttemptedCount: jobs.Sum(job => job.RecursiveUnpack?.AttemptedFileCount ?? 0),
			RecursiveUnpackUnpackedCount: jobs.Sum(job => job.RecursiveUnpack?.UnpackedFileCount ?? 0),
			RecursiveUnpackRetainedCount: jobs.Sum(job => job.RecursiveUnpack?.RetainedFileCount ?? 0),
			RecursiveUnpackFailedCount: jobs.Sum(job => job.RecursiveUnpack?.FailedFileCount ?? 0));
	}

	public static ExportJobOutcomeSummary BuildJobSummary(
		PrimarySkippedCollection[] skippedCollections,
		PrimaryFailedCollection[] failedCollections,
		RecursiveUnpackResultRecord[] recursiveUnpackResults)
	{
		return new ExportJobOutcomeSummary(
				SelectionSkipReasons: CountByKey(skippedCollections.Select(item => item.Reason)),
				ExportFailureReasons: CountByKey(failedCollections.Select(item => item.Reason)),
				RecursiveUnpackStatuses: CountByKey(recursiveUnpackResults.Select(item => item.Status)),
				RecursiveUnpackReasons: CountByKey(recursiveUnpackResults
					.Select(item => item.Reason)
					.Where(item => !string.IsNullOrWhiteSpace(item))
					.Select(item => item!)));
		}

	private static OutcomeReasonCount[] CountByKey(IEnumerable<string> values)
	{
		return values
			.GroupBy(value => string.IsNullOrWhiteSpace(value) ? "unknown" : value, StringComparer.Ordinal)
			.OrderByDescending(group => group.Count())
			.ThenBy(group => group.Key, StringComparer.Ordinal)
			.Select(group => new OutcomeReasonCount(group.Key, group.Count()))
			.ToArray();
	}
}
