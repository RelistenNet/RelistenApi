using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Relisten.Services.Popularity;

namespace Relisten.Api
{
    public class PopularitySortWindowModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            ArgumentNullException.ThrowIfNull(bindingContext);

            var valueResult = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (valueResult == ValueProviderResult.None)
            {
                bindingContext.Result = ModelBindingResult.Success(PopularitySortWindow.Days30);
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, valueResult);
            var raw = valueResult.FirstValue;
            bindingContext.Result = ModelBindingResult.Success(ParseSortWindow(raw));
            return Task.CompletedTask;
        }

        private static PopularitySortWindow ParseSortWindow(string? raw)
        {
            return raw?.Trim().ToLowerInvariant() switch
            {
                "48h" => PopularitySortWindow.Hours48,
                "7d" => PopularitySortWindow.Days7,
                "30d" => PopularitySortWindow.Days30,
                "hours48" => PopularitySortWindow.Hours48,
                "days7" => PopularitySortWindow.Days7,
                "days30" => PopularitySortWindow.Days30,
                _ => PopularitySortWindow.Days30
            };
        }
    }
}
