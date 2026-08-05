using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AndroidConnectUI;

internal static class ToolManager
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";
    private const string GitHubAccelerator = "https://ghfast.top/";

    private static readonly string ToolsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AndroidConnectUI", "tools");

    public static string AdbPath { get; private set; } = "adb";
    public static string ScrcpyPath { get; private set; } = "scrcpy";

    public static async Task EnsureToolsAsync(CancellationToken cancellationToken = default)
    {
        string? systemAdb = FindOnPath("adb.exe");
        string? systemScrcpy = FindOnPath("scrcpy.exe");

        string? localDirectory = FindLocalToolDirectory();
        if (localDirectory is null && (systemAdb is null || systemScrcpy is null))
        {
            localDirectory = await DownloadScrcpyAsync(cancellationToken);
        }

        AdbPath = localDirectory is not null
            ? Path.Combine(localDirectory, "adb.exe")
            : systemAdb ?? "adb";
        ScrcpyPath = localDirectory is not null
            ? Path.Combine(localDirectory, "scrcpy.exe")
            : systemScrcpy ?? "scrcpy";
    }

    private static string? FindLocalToolDirectory()
    {
        if (!Directory.Exists(ToolsRoot))
            return null;

        return Directory.EnumerateFiles(ToolsRoot, "scrcpy.exe", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(directory => directory is not null &&
                File.Exists(Path.Combine(directory, "adb.exe")));
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(entry.Trim().Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static async Task<string> DownloadScrcpyAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ToolsRoot);

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AndroidConnectUI/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using JsonDocument release = JsonDocument.Parse(
            await client.GetStringAsync(LatestReleaseApi, cancellationToken));

        JsonElement asset = release.RootElement.GetProperty("assets")
            .EnumerateArray()
            .FirstOrDefault(item =>
            {
                string name = item.GetProperty("name").GetString() ?? string.Empty;
                return name.StartsWith("scrcpy-win64-v", StringComparison.OrdinalIgnoreCase) &&
                       name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            });

        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("官方 scrcpy Release 中没有找到 Windows x64 压缩包。");

        string assetName = asset.GetProperty("name").GetString()!;
        string officialUrl = asset.GetProperty("browser_download_url").GetString()!;
        if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out Uri? officialUri) ||
            !officialUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !officialUri.AbsolutePath.StartsWith("/Genymobile/scrcpy/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("scrcpy Release 返回了非官方的下载地址。");
        string? expectedDigest = asset.TryGetProperty("digest", out JsonElement digestElement)
            ? digestElement.GetString()
            : null;

        string operationRoot = Path.Combine(Path.GetTempPath(), $"AndroidConnectUI-{Guid.NewGuid():N}");
        string zipPath = Path.Combine(operationRoot, assetName);
        string extractRoot = Path.Combine(operationRoot, "extract");
        Directory.CreateDirectory(operationRoot);

        try
        {
            try
            {
                await DownloadFileAsync(client, GitHubAccelerator + officialUrl, zipPath, cancellationToken);
            }
            catch (HttpRequestException)
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                await DownloadFileAsync(client, officialUrl, zipPath, cancellationToken);
            }
            VerifyDigestIfAvailable(zipPath, expectedDigest);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);

            string? scrcpyExe = Directory.EnumerateFiles(extractRoot, "scrcpy.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            string? sourceDirectory = scrcpyExe is null ? null : Path.GetDirectoryName(scrcpyExe);
            if (sourceDirectory is null || !File.Exists(Path.Combine(sourceDirectory, "adb.exe")))
                throw new InvalidDataException("下载的 scrcpy 包不完整，未找到 scrcpy.exe 和 adb.exe。");

            string version = release.RootElement.GetProperty("tag_name").GetString() ?? "latest";
            string destination = Path.Combine(ToolsRoot, SanitizeDirectoryName(version));
            if (!Directory.Exists(destination))
                Directory.Move(sourceDirectory, destination);

            if (!File.Exists(Path.Combine(destination, "adb.exe")) ||
                !File.Exists(Path.Combine(destination, "scrcpy.exe")))
                throw new InvalidDataException("本地 scrcpy 工具目录不完整。");

            return destination;
        }
        finally
        {
            try
            {
                if (Directory.Exists(operationRoot))
                    Directory.Delete(operationRoot, recursive: true);
            }
            catch (IOException)
            {
                // A locked temporary file can be cleaned up by Windows later.
            }
        }
    }

    private static async Task DownloadFileAsync(
        HttpClient client, string url, string destination, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void VerifyDigestIfAvailable(string filePath, string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            return;

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)));
        string expected = digest["sha256:".Length..];
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("scrcpy 下载文件的 SHA-256 校验失败。");
    }

    private static string SanitizeDirectoryName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }
}
