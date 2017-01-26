using System;
using System.Collections.Generic;
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
				bar.SetValue(100.0 * count / list.Count);

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
				bar.SetValue(100.0 * count / list.Count);

				count++;
			}

			return list;
		}
}
}
