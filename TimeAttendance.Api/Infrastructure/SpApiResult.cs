public sealed class SpApiResult
{
    public bool ok { get; set; }
    public string message { get; set; } = "";
    public object? data { get; set; }
    public int? errorCode { get; set; }
}
