namespace ProductSearch.Core.Services;

internal static class BootstrapExceptionFormatter
{
    public static (string Summary, string Detail) Format(Exception ex)
    {
        var summary = ex.Message;
        var detail = ex.ToString();
        return (summary, detail);
    }
}
