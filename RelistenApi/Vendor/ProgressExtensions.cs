using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire.Console.Progress;

namespace Relisten
{
	public static class ProgressExtensions
	{
		public static async Task<IList<T>> AsyncForEachWithProgress<T>(this IList<T> list, IProgressBar bar, Func<T, Task> action)
		{
			var count = 1;
			foreach (var item in list)
			{
				await action(item);
				bar?.SetValue(100.0 * count / list.Count);

				count++;
			}

			return list;
		}

		public static IList<T> ForEachWithProgress<T>(this IList<T> list, IProgressBar bar, Action<T> action)
		{
			var count = 1;
			foreach (var item in list)
			{
				action(item);
				bar?.SetValue(100.0 * count / list.Count);

				count++;
			}

			return list;
		}

		// http://stackoverflow.com/a/27325206/132509
		public static async Task ForEachAsync<TSource>(this IList<TSource> source, Func<TSource, Task> selector, IProgressBar bar, int maxDegreesOfParallelism)
		{
			var activeTasks = new HashSet<Task>();
			var count = 1;
			foreach (var item in source)
			{
				activeTasks.Add(selector(item));
				if (activeTasks.Count >= maxDegreesOfParallelism)
				{
					var completed = await Task.WhenAny(activeTasks);

					count++;
					bar?.SetValue(Math.Min(100.0,  100.0 * count / source.Count));

					activeTasks.Remove(completed);
				}
			}

			await Task.WhenAll(activeTasks);
		}
	}
}
