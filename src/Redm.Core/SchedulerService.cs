namespace Redm.Core;

public class SchedulerService
{
    private readonly Dictionary<string, DlQueue> _queues = new(StringComparer.OrdinalIgnoreCase) { ["main"] = new() { Name = "main" } };
    public IReadOnlyDictionary<string, DlQueue> Queues => _queues;
    public DlQueue GetOrCreate(string name)
    {
        if (!_queues.TryGetValue(name, out var q)) { q = new DlQueue { Name = name }; _queues[name] = q; }
        return q;
    }
    public void Start(string name = "main") => GetOrCreate(name).Start();
    public void Stop(string name = "main") => GetOrCreate(name).Stop();
}
