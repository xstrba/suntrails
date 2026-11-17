using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Godot;

namespace World;

internal sealed record PhotoDetails(DateTimeOffset TakenAt, float X, float Y, float Z,
    double FocalLength, double Aperture, double Shutter, bool AutoShutter, int Iso,
    double ExposureCompensation, double Focus, bool AutoFocus, bool Tripod);

internal sealed record PhotoEntry(string Path, DateTimeOffset Date, PhotoDetails Details,
    Vector2I Resolution, long FileSize)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

internal sealed class PhotoLibrary
{
    const string Preferences = "user://photo-library.cfg";
    public string Folder { get; private set; }

    public PhotoLibrary()
    {
        var config = new ConfigFile();
        if (config.Load(Preferences) == Error.Ok)
        {
            string saved = (string)config.GetValue("gallery", "folder", "");
            if (Path.IsPathFullyQualified(saved)) Folder = saved;
        }
        if (Folder != null) return;

        string pictures = OS.GetSystemDir(OS.SystemDir.Pictures);
        Folder = Path.Combine(string.IsNullOrEmpty(pictures) ? OS.GetUserDataDir() : pictures, "World Photos");
    }

    public void SetFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        // Try a temporary file so the user can pick another folder before the next shot.
        using (File.Create(TemporaryPath(folder), 4096, FileOptions.DeleteOnClose)) { }

        var config = new ConfigFile();
        config.SetValue("gallery", "folder", folder);
        if (config.Save(Preferences) != Error.Ok)
            throw new IOException("The folder choice couldn't be saved.");
        Folder = folder;
    }

    public string Save(Image image, PhotoDetails details)
    {
        Directory.CreateDirectory(Folder);
        byte[] png = image.SavePngToBuffer();
        if (png.Length <= PngPhotoDetails.HeaderSize)
            throw new IOException("The photo couldn't be encoded.");

        string stem = $"world-photo-{details.TakenAt.LocalDateTime:yyyyMMdd-HHmmss-fff}";
        string temporary = TemporaryPath(Folder);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.Write))
            {
                stream.Write(png.AsSpan(0, PngPhotoDetails.HeaderSize));
                PngPhotoDetails.Write(stream, details);
                stream.Write(png.AsSpan(PngPhotoDetails.HeaderSize));
            }

            string destination = Path.Combine(Folder, stem + ".png");
            for (int suffix = 2; ; suffix++)
            {
                try
                {
                    File.Move(temporary, destination);
                    return destination;
                }
                catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
                {
                    destination = Path.Combine(Folder, $"{stem}-{suffix}.png");
                }
            }
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static List<PhotoEntry> ReadFolder(string folder, CancellationToken cancellation)
    {
        var entries = new List<PhotoEntry>();
        foreach (string path in Directory.EnumerateFiles(folder))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var details = PngPhotoDetails.Read(stream, out var resolution);
                entries.Add(new PhotoEntry(path, details?.TakenAt ?? File.GetLastWriteTimeUtc(path),
                    details, resolution, stream.Length));
            }
            catch (Exception e) when (IsFileError(e))
            {
                // A file can disappear or become unavailable while the folder is being read.
            }
        }
        return entries;
    }

    public static PhotoEntry Rename(PhotoEntry entry, string name)
    {
        name = name.Trim();
        if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4].TrimEnd();
        if (name.Length == 0) throw new ArgumentException("Enter a name for this photo.");
        if (name.EndsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.IndexOfAny("/\\:*?\"<>|".ToCharArray()) >= 0)
            throw new ArgumentException("Use a name without slashes, special symbols, or a dot at the end.");

        string folder = Path.GetDirectoryName(entry.Path);
        string destination = Path.Combine(folder, name + Path.GetExtension(entry.Path));
        if (string.Equals(entry.Path, destination, StringComparison.Ordinal)) return entry;

        try { File.Move(entry.Path, destination); }
        catch (IOException) when (File.Exists(destination))
        {
            throw new ArgumentException("A photo already has that name. Try another name.");
        }
        return entry with { Path = destination };
    }

    public static bool IsFileError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    static string TemporaryPath(string folder) => Path.Combine(folder, $".world-photo-{Guid.NewGuid():N}.tmp");
}

internal static class PngPhotoDetails
{
    // PNG signature and the mandatory, fixed-size IHDR chunk.
    public const int HeaderSize = 33;
    static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    static ReadOnlySpan<byte> Keyword => "World Photo\0"u8;
    static ReadOnlySpan<byte> TextChunk => "tEXt"u8;

    public static void Write(Stream stream, PhotoDetails details)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(details);
        Span<byte> chunk = stackalloc byte[4 + Keyword.Length + json.Length];
        TextChunk.CopyTo(chunk);
        Keyword.CopyTo(chunk[4..]);
        json.CopyTo(chunk[(4 + Keyword.Length)..]);

        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, chunk.Length - 4);
        stream.Write(number);
        stream.Write(chunk);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(chunk));
        stream.Write(number);
    }

    public static PhotoDetails Read(Stream stream, out Vector2I resolution)
    {
        resolution = Vector2I.Zero;
        if (stream.Length < HeaderSize) return null;
        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(Signature) || !header[12..16].SequenceEqual("IHDR"u8) ||
            BinaryPrimitives.ReadInt32BigEndian(header[8..12]) != 13) return null;
        int width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        int height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (width <= 0 || height <= 0) return null;
        resolution = new Vector2I(width, height);

        // Photos from this game put their metadata directly after IHDR. Never decode the image here.
        if (stream.Length < HeaderSize + 8) return null;
        Span<byte> chunkHeader = stackalloc byte[8];
        stream.ReadExactly(chunkHeader);
        int length = BinaryPrimitives.ReadInt32BigEndian(chunkHeader[..4]);
        if (!chunkHeader[4..].SequenceEqual(TextChunk) || length <= Keyword.Length || length > 4096 ||
            stream.Length - stream.Position < length + 4) return null;

        Span<byte> chunk = stackalloc byte[length + 4];
        TextChunk.CopyTo(chunk);
        stream.ReadExactly(chunk[4..]);
        if (!chunk[4..].StartsWith(Keyword)) return null;

        Span<byte> checksum = stackalloc byte[4];
        stream.ReadExactly(checksum);
        if (BinaryPrimitives.ReadUInt32BigEndian(checksum) != Crc(chunk)) return null;

        try
        {
            var details = JsonSerializer.Deserialize<PhotoDetails>(chunk[(4 + Keyword.Length)..]);
            return details != null && details.TakenAt != default && details.Shutter > 0 ? details : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // PNG's CRC-32 covers the chunk type and data, excluding the length field.
    static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }
}
