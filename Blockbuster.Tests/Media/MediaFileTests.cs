using Blockbuster.Core.Media;
using Xunit;

namespace Blockbuster.Tests.Media;

public sealed class MediaFileTests
{
    [Fact]
    public void MediaFilePreservesLibraryRelativePath()
    {
        var mediaFile = new MediaFile(
            Guid.NewGuid(),
            "movies-main",
            MediaKind.Movie,
            Path.Combine("Classics", "Movie (1999).mp4"),
            42,
            DateTimeOffset.UtcNow,
            true);

        Assert.False(Path.IsPathRooted(mediaFile.RelativePath));
        Assert.Equal(MediaKind.Movie, mediaFile.Kind);
    }
}
