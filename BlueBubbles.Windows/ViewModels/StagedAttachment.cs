namespace BlueBubbles.Windows.ViewModels;

public class StagedAttachment
{
    public string FilePath { get; }
    public string FileName { get; }
    public long FileSize { get; }

    public StagedAttachment(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);

        try { FileSize = new FileInfo(filePath).Length; }
        catch { FileSize = 0; }
    }

    public string FormattedSize => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F1} MB"
    };
}
