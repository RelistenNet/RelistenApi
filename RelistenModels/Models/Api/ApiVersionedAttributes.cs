namespace Relisten.Api.Models.Api
{
    [AttributeUsage(AttributeTargets.Property)]
    public class V3JsonOnlyAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Property)]
    public class V2JsonOnlyAttribute : Attribute
    {
    }
}
