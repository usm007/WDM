namespace WDM.Services;

/// <summary>An updater failure whose message is safe and useful to show to the
/// user (wrong feed flavor, corrupt/truncated download, checksum mismatch,
/// installer refused). Transport errors stay generic in the UI.</summary>
public sealed class UpdateException : InvalidOperationException
{
    public UpdateException(string message) : base(message) { }
    public UpdateException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Outcome of starting a downloaded installer.</summary>
public enum InstallerLaunchStatus
{
    /// <summary>The installer could not be started at all.</summary>
    NotStarted,
    /// <summary>The installer process is running; WDM may close so files can be replaced.</summary>
    Running,
    /// <summary>The installer finished immediately with exit code 0.</summary>
    Completed,
    /// <summary>The installer exited immediately with a failure code: do not close WDM.</summary>
    Failed,
}

/// <summary>Result of <see cref="UpdateChecker.LaunchInstallerAndWaitForStart"/>.</summary>
public sealed record InstallerLaunchResult(InstallerLaunchStatus Status, int? ExitCode, System.Diagnostics.Process? Process)
{
    /// <summary>True when WDM should shut down so the installer can replace its files.</summary>
    public bool CanExitApp => Status is InstallerLaunchStatus.Running or InstallerLaunchStatus.Completed;
}
