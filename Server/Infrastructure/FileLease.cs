namespace Portster;

internal static class FileLease
{
    public static FileStream Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(directory, ".portster.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (deadline.ElapsedMilliseconds < 1000)
            {
                Thread.Sleep(20);
            }
            catch (IOException)
            {
                throw new PortsterException("STORE_BUSY", "Another process holds the storage lock. Retry later.");
            }
        }
    }
}
