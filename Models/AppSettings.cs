namespace DicomMover.Models;

public sealed class AppSettings
{
    public string WatchFolder { get; set; } = @"C:\testdicom";
    public bool SearchSubfolders { get; set; } = true;
    public int ScanIntervalSeconds { get; set; } = 5;
    public int FileStableSeconds { get; set; } = 3;
    public int RetryFailedAfterSeconds { get; set; } = 60;

    public string LocalAeTitle { get; set; } = "DICOMMOVER";
    public string RemoteAeTitle { get; set; } = "ESKIMOS";
    public string RemoteHost { get; set; } = "192.168.88.230";
    public int RemotePort { get; set; } = 4242;

    public string DatabaseFile { get; set; } = @"data\dicommover.db";
    public string LogFile { get; set; } = @"logs\dicommover.log";
}
