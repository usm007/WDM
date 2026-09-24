namespace WDM.Services;

/// <summary>One chunk's resume state: byte range plus completion flag.</summary>
public sealed class SegmentRecord
{
    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public bool Done { get; set; }
}
