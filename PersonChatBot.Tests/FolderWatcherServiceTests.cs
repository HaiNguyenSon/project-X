using PersonChatBot.Watching;

namespace PersonChatBot.Tests;

public class FolderWatcherServiceTests
{
    [Theory]
    [InlineData(typeof(IOException))]                 // file locked / still being written
    [InlineData(typeof(UnauthorizedAccessException))] // temporarily inaccessible
    [InlineData(typeof(FileNotFoundException))]        // (derives from IOException) vanished mid-process
    public void IO_errors_are_transient(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.True(FolderWatcherService.IsTransient(ex));
    }

    [Theory]
    [InlineData(typeof(FormatException))]              // corrupt content
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    public void Parse_or_format_errors_are_not_transient(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        Assert.False(FolderWatcherService.IsTransient(ex));
    }
}
