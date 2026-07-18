namespace Relisten.Import
{
    public class ImportOptions
    {
        public static readonly ImportOptions Default = new();

        public int? OnlyYear { get; init; }
        public bool IsThinScrape => OnlyYear.HasValue;
    }
}
