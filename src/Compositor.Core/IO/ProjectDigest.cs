using System.Security.Cryptography;
using System.Text;

namespace Compositor.Core.IO;

/// <summary>
/// What a project package holds, in one hash: its manifest and the names and sizes of its images. Compared rather
/// than modification dates, since sync clients touch metadata without changing anything. Mirrors ProjectDigest.swift.
/// </summary>
public static class ProjectDigest
{
    /// <summary>
    /// The package's digest, or null when it can't be read (caught half written, or gone): the caller then waits for
    /// the next change.
    /// </summary>
    public static string? Compute(string path)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(File.ReadAllBytes(Path.Combine(path, ProjectManifest.ManifestName)));
            var images = Path.Combine(path, ProjectManifest.ImagesFolder);
            if (Directory.Exists(images))
            {
                foreach (var file in Directory.GetFiles(images).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file)));
                    hash.AppendData(BitConverter.GetBytes((ulong)new FileInfo(file).Length));
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception) { return null; }
    }
}
