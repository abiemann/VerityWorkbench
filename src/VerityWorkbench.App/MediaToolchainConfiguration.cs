using System.Text.Json;

namespace VerityWorkbench.App;

internal sealed record ConfiguredMediaToolchain(
    string ValidationContractVersion,
    string BuildIdentity,
    string License,
    string FfmpegExecutablePath,
    string FfmpegSha256,
    string FfprobeExecutablePath,
    string FfprobeSha256);

internal static class MediaToolchainConfiguration
{
    private const string FfmpegRootEnvironmentVariable = "VERITYWORKBENCH_FFMPEG_ROOT";
    private const int MaximumConfigurationBytes = 64 * 1024;

    public static ConfiguredMediaToolchain Load()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "media-tools.manifest.json");
        using var manifest = ReadJson(manifestPath, "The approved media-tools manifest is unavailable.");
        var manifestRoot = manifest.RootElement;
        if (manifestRoot.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("The media-tools manifest schema is unsupported.");
        }

        var configuredRoot = Environment.GetEnvironmentVariable(FfmpegRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            var localSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
            using var localSettings = ReadJson(
                localSettingsPath,
                $"Media tools are not configured. Set {FfmpegRootEnvironmentVariable} or create appsettings.Local.json.");
            configuredRoot = localSettings.RootElement
                .GetProperty("mediaTools")
                .GetProperty("ffmpegRoot")
                .GetString();
        }

        if (string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathFullyQualified(configuredRoot))
        {
            throw new InvalidDataException("The configured FFmpeg root must be an absolute path.");
        }

        var root = Path.GetFullPath(configuredRoot);
        var ffmpeg = manifestRoot.GetProperty("ffmpeg");
        var ffprobe = manifestRoot.GetProperty("ffprobe");
        return new ConfiguredMediaToolchain(
            manifestRoot.GetProperty("validationContractVersion").GetString()
                ?? throw new InvalidDataException("The media validation contract is missing."),
            manifestRoot.GetProperty("buildIdentity").GetString()
                ?? throw new InvalidDataException("The approved FFmpeg build identity is missing."),
            manifestRoot.GetProperty("license").GetString()
                ?? throw new InvalidDataException("The approved FFmpeg license declaration is missing."),
            BuildExecutablePath(root, ffmpeg),
            ReadSha256(ffmpeg),
            BuildExecutablePath(root, ffprobe),
            ReadSha256(ffprobe));
    }

    private static JsonDocument ReadJson(string path, string missingMessage)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(missingMessage);
        }

        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaximumConfigurationBytes)
        {
            throw new InvalidDataException("A media-tools configuration file has an invalid size.");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        return JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
    }

    private static string BuildExecutablePath(string root, JsonElement executable)
    {
        var fileName = executable.GetProperty("fileName").GetString();
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidDataException("A media-tools executable name is invalid.");
        }

        return Path.GetFullPath(Path.Combine(root, "bin", fileName));
    }

    private static string ReadSha256(JsonElement executable)
    {
        var hash = executable.GetProperty("sha256").GetString();
        if (hash is null
            || hash.Length != 64
            || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("A media-tools executable hash is invalid.");
        }

        return hash;
    }
}
