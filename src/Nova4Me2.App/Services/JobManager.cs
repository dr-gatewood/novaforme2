using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nova4Me2.Core.Ntfs;
using Nova4Me2.Core.Recovery;
using Nova4Me2.Core.Reports;
using Nova4Me2.Core.Util;

namespace Nova4Me2.App.Services;

public enum JobStatus { Queued, Running, Done, Failed, Cancelled }

public sealed class CopyJob : INotifyPropertyChanged
{
    private static int _next;
    public int Id { get; } = Interlocked.Increment(ref _next);
    public string Name { get; init; } = "";
    public List<NtfsEntry> Roots { get; init; } = new();
    public string Destination { get; init; } = "";
    public CopyOptions Options { get; init; } = new();
    public IDirectorySource Source { get; init; } = null!;
    public CancellationTokenSource Cts { get; } = new();
    public CopyProgress Progress { get; private set; } = new();
    public JobStatus Status { get; private set; } = JobStatus.Queued;
    public string Error { get; private set; } = "";
    public DateTime Created { get; } = DateTime.Now;
    public Report? Report { get; private set; }

    public string StatusText => Status switch
    {
        JobStatus.Queued => "Queued",
        JobStatus.Running => Progress.Enumerating ? $"Scanning… {Progress.FilesTotal:N0} files" : $"{Progress.Fraction * 100:0.0}%  {Progress.FilesDone:N0}/{Progress.FilesTotal:N0} files  {Format.Rate(Progress.BytesPerSecond)}{(Progress.Eta is { } e ? $"  ETA {Format.Duration(e)}" : "")}",
        JobStatus.Done => $"Done: {Progress.FilesDone - Progress.FilesFailed - Progress.FilesSkipped:N0} files, {Format.Bytes(Progress.BytesDone)} in {Format.Duration(Progress.Elapsed)}{(Progress.FilesPartial > 0 ? $", {Progress.FilesPartial} partial" : "")}{(Progress.FilesFailed > 0 ? $", {Progress.FilesFailed} failed" : "")}",
        JobStatus.Failed => "Failed: " + Error,
        _ => "Cancelled"
    };
    public double Percent => Progress.Fraction * 100;
    public string CurrentFile => Progress.CurrentFile;
    public bool IsActive => Status is JobStatus.Queued or JobStatus.Running;
    public bool IsFinished => !IsActive;
    public int ErrorCount => Progress.Errors.Count;

    internal void Update(CopyProgress p) { Progress = p; Notify(); }
    internal void SetStatus(JobStatus s, string error = "")
    {
        Status = s; Error = error;
        if (s is JobStatus.Done or JobStatus.Failed or JobStatus.Cancelled) Report = ReportBuilder.FromCopy(Progress, Name, Destination);
        Notify();
    }
    private void Notify()
    {
        foreach (var n in new[] { nameof(StatusText), nameof(Percent), nameof(CurrentFile), nameof(IsActive), nameof(IsFinished), nameof(ErrorCount), nameof(Status), nameof(Progress) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Runs copy jobs one at a time (the drive is the bottleneck) on a background thread, reporting on the UI thread.</summary>
public sealed class JobManager
{
    public ObservableCollection<CopyJob> Jobs { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public event Action<CopyJob>? JobFinished;

    public CopyJob Enqueue(IDirectorySource src, IEnumerable<NtfsEntry> roots, string dest, CopyOptions opt, string name)
    {
        var job = new CopyJob { Name = name, Roots = roots.ToList(), Destination = dest, Options = opt, Source = src };
        Jobs.Insert(0, job);
        _ = RunAsync(job);
        return job;
    }

    private async Task RunAsync(CopyJob job)
    {
        await _gate.WaitAsync();
        try
        {
            if (job.Cts.IsCancellationRequested) { job.SetStatus(JobStatus.Cancelled); return; }
            job.SetStatus(JobStatus.Running);
            var ui = System.Windows.Application.Current?.Dispatcher;
            var progress = new Progress<CopyProgress>(p => job.Update(p));
            try
            {
                var result = await Task.Run(() => new Extractor(job.Source).Copy(job.Roots, job.Destination, job.Options, progress, job.Cts.Token));
                job.Update(result);
                job.SetStatus(JobStatus.Done);
            }
            catch (OperationCanceledException) { job.SetStatus(JobStatus.Cancelled); }
            catch (Exception ex) { Log.Error($"Job {job.Name} failed: {ex.Message}"); job.SetStatus(JobStatus.Failed, ex.Message); }
            JobFinished?.Invoke(job);
        }
        finally { _gate.Release(); }
    }

    public bool AnyActive => Jobs.Any(j => j.IsActive);
}
