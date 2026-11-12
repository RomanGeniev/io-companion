using System;
using System.IO;
using System.Text.RegularExpressions;

namespace GraphiteCompanion.Services;

public static class GalleryRename
{
    public static string Destination(GalleryItem item, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') || name.Length > 180 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase))
            throw new IOException("Введите имя без служебных символов, точки или пробела в конце (до 180 знаков).");
        return Path.Combine(Path.GetDirectoryName(item.Path)!, name + Path.GetExtension(item.Path));
    }
    public static string Rename(string root, GalleryItem item, string name)
    {
        string source = Path.GetFullPath(item.Path), destination = Destination(item, name);
        string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) || !GalleryService.IsMediaFile(source)) throw new IOException("Файл находится вне папки галереи.");
        for (string? part = source; part != null && part.Length >= basePath.Length - 1; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Переименование через ссылки не поддерживается.");
        var info = new FileInfo(source);
        if (info.Length != item.Bytes || info.LastWriteTimeUtc != item.Modified) throw new IOException("Файл изменился. Обновите галерею.");
        if (source == destination) return source;
        if (!source.Equals(destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination)) throw new IOException("Такое имя уже занято. Выберите другое.");
        File.Move(source, destination, false);
        return destination;
    }
}
