using System;
using System.IO;

namespace Relisten.Normalization;

/// Embedded normalization DDL. Files 013/014 retain their string-theory sources;
/// later migrations also own derived catalog storage here. RelistenApi runs these
/// files in its shared migration sequence.
public static class NormalizationSql
{
    public static string Read(string fileName)
    {
        var assembly = typeof(NormalizationSql).Assembly;
        var name = $"{assembly.GetName().Name}.Sql.{fileName}";
        using var stream =
            assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing embedded normalization SQL {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
