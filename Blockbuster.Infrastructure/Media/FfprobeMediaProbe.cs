using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Blockbuster.Core.Media;
using Blockbuster.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Blockbuster.Infrastructure.Media;

public sealed class FfprobeMediaProbe(
    IOptions<MediaProbeOptions> options,
    IExternalProcessRunner processes) : IMediaProbe
{
    private readonly MediaProbeOptions _options = options.Value;

    public async Task<MediaProbeResult> ProbeAsync(
        string absolutePath,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new ArgumentException(
                "ffprobe requires an absolute media path.",
                nameof(absolutePath));
        }

        var startInfo = CreateStartInfo(absolutePath);
        ProcessExecutionResult execution;
        try
        {
            execution = await processes.RunAsync(
                startInfo,
                _options.Timeout,
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Unable to start ffprobe at '{_options.ExecutablePath}'.",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"ffprobe exceeded the configured timeout of {_options.Timeout}.",
                exception);
        }

        if (execution.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"ffprobe exited with code {execution.ExitCode}: "
                + TrimError(execution.StandardError));
        }

        return Parse(execution.StandardOutput);
    }

    private ProcessStartInfo CreateStartInfo(string absolutePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
        {
            "-v",
            "error",
            "-show_format",
            "-show_streams",
            "-of",
            "json",
            absolutePath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static MediaProbeResult Parse(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var format = root.TryGetProperty("format", out var formatElement)
                ? formatElement
                : default;
            var duration = ReadDouble(format, "duration");
            var container = ReadString(format, "format_name")?
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            JsonElement? video = null;
            JsonElement? audio = null;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = ReadString(stream, "codec_type");
                    if (type == "video" && video is null)
                    {
                        video = stream.Clone();
                    }

                    if (type == "audio" && audio is null)
                    {
                        audio = stream.Clone();
                    }
                }
            }

            duration ??= video is null
                ? null
                : ReadDouble(video.Value, "duration");
            return new MediaProbeResult(
                TimeSpan.FromSeconds(Math.Max(0, duration ?? 0)),
                container,
                video is null ? null : ReadString(video.Value, "codec_name"),
                audio is null ? null : ReadString(audio.Value, "codec_name"),
                video is null ? null : ReadInt(video.Value, "width"),
                video is null ? null : ReadInt(video.Value, "height"),
                audio is null ? null : ReadInt(audio.Value, "channels"));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "ffprobe returned invalid JSON.",
                exception);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        double.TryParse(
            ReadString(element, name),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string TrimError(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no diagnostic output";
        }

        var trimmed = value.Trim();
        return trimmed[..Math.Min(1000, trimmed.Length)];
    }
}
