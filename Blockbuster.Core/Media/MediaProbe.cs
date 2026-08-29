namespace Blockbuster.Core.Media;

public sealed record MediaProbeResult(
    TimeSpan Duration,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    int? Width,
    int? Height,
    int? AudioChannels);

public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken = default);
}
