using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AssetRipper.Tools.ExportRunner;

internal sealed class ExportScheduler
{
	public async Task<ExportJobManifest[]> ExecuteAsync(
		ExportPlan plan,
		Func<PlannedExportJob, ExportJobManifest> executeJob,
		Action<PlannedExportJob, ExportJobManifest>? onJobCompleted,
		int workerCount,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(executeJob);

		workerCount = Math.Max(1, workerCount);
		Channel<PlannedExportJob> channel = Channel.CreateBounded<PlannedExportJob>(new BoundedChannelOptions(Math.Max(1, workerCount * 2))
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleWriter = true,
			SingleReader = workerCount == 1,
		});

		ConcurrentBag<ExportJobManifest> results = [];

		Task producer = Task.Run(async () =>
		{
			try
			{
				foreach (PlannedExportJob job in plan.Jobs)
				{
					await channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
				}
			}
			finally
			{
				channel.Writer.TryComplete();
			}
		}, cancellationToken);

		Task[] workers = Enumerable.Range(0, workerCount)
			.Select(_ => Task.Run(async () =>
			{
				await foreach (PlannedExportJob job in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
				{
					ExportJobManifest result = executeJob(job);
					results.Add(result);
					onJobCompleted?.Invoke(job, result);
				}
			}, cancellationToken))
			.ToArray();

		await producer.ConfigureAwait(false);
		await Task.WhenAll(workers).ConfigureAwait(false);

		Dictionary<string, int> orderLookup = plan.Jobs
			.Select((job, index) => new { job.Name, Index = index })
			.ToDictionary(item => item.Name, item => item.Index, StringComparer.Ordinal);

		return results
			.OrderBy(result => orderLookup.GetValueOrDefault(result.Name, int.MaxValue))
			.ToArray();
	}
}
