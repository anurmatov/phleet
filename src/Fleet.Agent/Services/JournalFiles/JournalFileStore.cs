using System.Security.Cryptography;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;

namespace Fleet.Agent.Services.JournalFiles;

/// <summary>Private verified downloads, never prompt input or outbound media.</summary>
public sealed class JournalFileStore(string attachmentDir, TimeProvider? time = null)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    public const long QuotaBytes = 200L * 1024 * 1024;
    private readonly string _directory = Path.Combine(Path.GetFullPath(attachmentDir), "journal");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<string> WriteAsync(string messageId, int ordinal, string mime, long length,
        string digest, Stream content, CancellationToken ct)
    {
        if (!Ulid.IsValid(messageId) || ordinal is < 0 or > 255 || length < 0
            || length > JournalAttachmentRequest.MaxBytes || digest.Length != 64
            || digest.Any(c => !char.IsAsciiHexDigit(c))) throw new JournalFileIntegrityException();
        Sweep();
        var name = messageId.ToUpperInvariant() + "-" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + Extension(mime);
        var final = Path.Combine(_directory, name);
        RejectLinks(final);
        ReserveSpace(length, final, null);
        var temporary = Path.Combine(_directory, "." + name + "." + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous, BufferSize = 64 * 1024 };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode;
            await using (var output = new FileStream(temporary, options))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024]; long received = 0;
                while (true)
                {
                    int read;
                    try { read = await content.ReadAsync(buffer, ct); }
                    catch (IOException) { throw new JournalFileIntegrityException(); }
                    if (read == 0) break;
                    received += read;
                    if (received > length || received > JournalAttachmentRequest.MaxBytes) throw new JournalFileIntegrityException();
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (received != length || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), digest, StringComparison.OrdinalIgnoreCase))
                    throw new JournalFileIntegrityException();
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            RejectLinks(_directory); RejectLinks(final);
            ReserveSpace(length, final, temporary);
            File.Move(temporary, final, overwrite: true);
            File.SetLastWriteTimeUtc(final, _time.GetUtcNow().UtcDateTime);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Sweep()
    {
        // Also reject links in parents. Root inside the container remains a trust boundary.
        RejectLinks(_directory);
        if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory, DirectoryMode);
        else Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, DirectoryMode);
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            RejectLinks(file);
            if (_time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(file) >= Ttl) File.Delete(file);
        }
        ReserveSpace(0, null, null);
    }

    private void ReserveSpace(long incoming, string? replacement, string? temporary)
    {
        var files = Directory.EnumerateFiles(_directory).Where(path => path != temporary && path != replacement)
            .Select(path => { RejectLinks(path); return new FileInfo(path); }).OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var total = files.Sum(file => file.Length)
            + (replacement is not null && File.Exists(replacement) ? new FileInfo(replacement).Length : 0);
        foreach (var file in files)
        {
            if (total + incoming <= QuotaBytes) break;
            total -= file.Length; file.Delete();
        }
        if (total + incoming > QuotaBytes) throw new IOException("journal_file_quota_exceeded");
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            // GetAttributes also finds dangling links, unlike File.Exists.
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("journal_file_link_refused"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string Extension(string mime) => mime switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp",
        "application/pdf" => ".pdf", "audio/ogg" => ".ogg", "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a",
        "video/mp4" => ".mp4", "text/plain" => ".txt", _ => ".bin",
    };
}

public sealed class JournalFileIntegrityException : Exception;
