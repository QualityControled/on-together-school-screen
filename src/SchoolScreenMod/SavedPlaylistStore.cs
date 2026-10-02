using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OnTogetherSchoolScreen
{
    internal sealed class SavedPlaylistEntry
    {
        public string VideoId { get; }
        public string Title { get; }

        public SavedPlaylistEntry(string videoId, string title = "")
        {
            VideoId = videoId ?? "";
            Title = title ?? "";
        }
    }

    internal sealed class SavedPlaylist
    {
        public string Name { get; }
        public IReadOnlyList<SavedPlaylistEntry> Entries { get; }

        internal SavedPlaylist(string name, IEnumerable<SavedPlaylistEntry> entries)
        {
            Name = name;
            Entries = new ReadOnlyCollection<SavedPlaylistEntry>(entries.ToList());
        }
    }

    // Only video IDs and cached titles are stored locally. Loading a playlist into a lobby
    // should still use the normal queue rules for each entry.
    internal sealed class SavedPlaylistStore
    {
        public const int MaxPlaylists = 20;
        public const int MaxEntries = 30;
        public const int MaxNameLength = 48;
        public const int MaxTitleLength = 512;
        private const int MaxFileBytes = 1024 * 1024;
        private const string FileName = "OnTogetherSchoolScreen.playlists.xml";
        private static readonly object StoreLock = new object();

        public string FilePath { get; }

        public SavedPlaylistStore(string configDirectory)
        {
            if (string.IsNullOrWhiteSpace(configDirectory))
                throw new ArgumentException("A config directory is required.", nameof(configDirectory));
            FilePath = Path.Combine(Path.GetFullPath(configDirectory), FileName);
        }

        // A missing file is an empty collection. A malformed file returns false and is
        // retained for recovery; saves and deletes refuse to overwrite it.
        public bool TryLoad(out IReadOnlyList<SavedPlaylist> playlists, out string error)
        {
            lock (StoreLock)
            {
                if (!TryRead(out var loaded, out _, out error))
                {
                    playlists = Array.Empty<SavedPlaylist>();
                    return false;
                }
                playlists = new ReadOnlyCollection<SavedPlaylist>(loaded);
                return true;
            }
        }

        // Names match without regard to case. Updating keeps the playlist's position;
        // duplicate video IDs are retained in their original order.
        public bool TrySave(string name, IEnumerable<SavedPlaylistEntry> entries, out string error)
        {
            if (!TryNormalizeName(name, out var normalizedName, out error)) return false;
            if (!TryNormalizeEntries(entries, out var normalizedEntries, out error)) return false;

            lock (StoreLock)
            {
                if (!TryRead(out var playlists, out var original, out error)) return false;
                int index = playlists.FindIndex(item => string.Equals(item.Name, normalizedName,
                    StringComparison.OrdinalIgnoreCase));
                var saved = new SavedPlaylist(normalizedName, normalizedEntries);
                if (index >= 0) playlists[index] = saved;
                else
                {
                    if (playlists.Count >= MaxPlaylists)
                    {
                        error = "You can save up to " + MaxPlaylists + " playlists. Delete one before saving another.";
                        return false;
                    }
                    playlists.Add(saved);
                }
                return TryWrite(playlists, original, out error);
            }
        }

        public bool TryDelete(string name, out string error)
        {
            if (!TryNormalizeName(name, out var normalizedName, out error)) return false;
            lock (StoreLock)
            {
                if (!TryRead(out var playlists, out var original, out error)) return false;
                int index = playlists.FindIndex(item => string.Equals(item.Name, normalizedName,
                    StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    error = "No saved playlist named \"" + normalizedName + "\" was found.";
                    return false;
                }
                playlists.RemoveAt(index);
                return TryWrite(playlists, original, out error);
            }
        }

        private bool TryRead(out List<SavedPlaylist> playlists, out byte[] original, out string error)
        {
            playlists = new List<SavedPlaylist>();
            original = null;
            error = "";
            try
            {
                original = ReadFileBytes(FilePath);
                if (original == null) return true;
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxFileBytes,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true
                };
                XDocument document;
                using (var stream = new MemoryStream(original, false))
                using (var reader = XmlReader.Create(stream, settings))
                    document = XDocument.Load(reader);
                var root = document.Root;
                if (root == null || root.Name != "savedPlaylists" ||
                    (string)root.Attribute("version") != "1" ||
                    root.Attributes().Any(attribute => attribute.Name != "version"))
                    throw new InvalidDataException("The saved-playlist format is not supported.");
                RequireElementsOnly(root);
                foreach (var element in root.Elements())
                {
                    if (element.Name != "playlist" || element.Attributes().Any(attribute => attribute.Name != "name"))
                        throw new InvalidDataException("The saved-playlist file contains an unexpected item.");
                    if (!TryNormalizeName((string)element.Attribute("name"), out var name, out var validationError))
                        throw new InvalidDataException(validationError);
                    if (playlists.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("The saved-playlist file contains duplicate playlist names.");
                    RequireElementsOnly(element);
                    var entries = new List<SavedPlaylistEntry>();
                    foreach (var video in element.Elements())
                    {
                        if (video.Name != "video" || video.HasElements ||
                            video.Attributes().Any(attribute => attribute.Name != "id" && attribute.Name != "title") ||
                            !string.IsNullOrWhiteSpace(video.Value))
                            throw new InvalidDataException("The saved-playlist file contains an unexpected video item.");
                        entries.Add(new SavedPlaylistEntry((string)video.Attribute("id"), (string)video.Attribute("title")));
                        if (entries.Count > MaxEntries)
                            throw new InvalidDataException("A saved playlist contains more than " + MaxEntries + " videos.");
                    }
                    if (!TryNormalizeEntries(entries, out var normalized, out validationError))
                        throw new InvalidDataException(validationError);
                    playlists.Add(new SavedPlaylist(name, normalized));
                    if (playlists.Count > MaxPlaylists)
                        throw new InvalidDataException("The file contains more than " + MaxPlaylists + " saved playlists.");
                }
                return true;
            }
            catch (Exception exception) when (IsStorageError(exception))
            {
                playlists.Clear();
                error = "Saved playlists could not be read. The existing file was left unchanged: " +
                    exception.Message + " File: " + FilePath;
                return false;
            }
        }

        private bool TryWrite(List<SavedPlaylist> playlists, byte[] original, out string error)
        {
            string temporaryPath = null;
            error = "";
            try
            {
                string directory = Path.GetDirectoryName(FilePath);
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
                var root = new XElement("savedPlaylists", new XAttribute("version", "1"),
                    playlists.Select(playlist => new XElement("playlist", new XAttribute("name", playlist.Name),
                        playlist.Entries.Select(entry => new XElement("video",
                            new XAttribute("id", entry.VideoId), new XAttribute("title", entry.Title))))));
                var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = XmlWriter.Create(stream, settings))
                    {
                        new XDocument(root).Save(writer);
                        writer.Flush();
                    }
                    stream.Flush(true);
                    if (stream.Length > MaxFileBytes)
                        throw new InvalidDataException("The saved-playlist collection is too large to save.");
                }
                var latest = ReadFileBytes(FilePath);
                if (!SameBytes(original, latest))
                {
                    error = "Saved playlists changed while saving. Reload them and try again.";
                    return false;
                }
                // Replace is atomic on Windows. Keep the preceding valid store in a .bak
                // file; if replacement is unsupported, fail without deleting the original.
                if (original == null) File.Move(temporaryPath, FilePath);
                else File.Replace(temporaryPath, FilePath, FilePath + ".bak");
                temporaryPath = null;
                return true;
            }
            catch (Exception exception) when (IsStorageError(exception))
            {
                error = "Saved playlists could not be saved. Your existing playlists were kept: " +
                    exception.Message + " File: " + FilePath;
                return false;
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static byte[] ReadFileBytes(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaxFileBytes)
                        throw new InvalidDataException("The saved-playlist file is too large.");
                    using (var buffer = new MemoryStream())
                    {
                        byte[] block = new byte[4096];
                        int count;
                        while ((count = stream.Read(block, 0, block.Length)) > 0)
                        {
                            if (buffer.Length + count > MaxFileBytes)
                                throw new InvalidDataException("The saved-playlist file is too large.");
                            buffer.Write(block, 0, count);
                        }
                        return buffer.ToArray();
                    }
                }
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static bool TryNormalizeName(string name, out string normalized, out string error)
        {
            normalized = (name ?? "").Trim();
            error = "";
            if (normalized.Length == 0 || normalized.Length > MaxNameLength)
            {
                error = "Give the playlist a name between 1 and " + MaxNameLength + " characters.";
                return false;
            }
            if (normalized.Any(char.IsControl) || !HasValidXmlCharacters(normalized))
            {
                error = "The playlist name contains unsupported characters.";
                return false;
            }
            return true;
        }

        private static bool TryNormalizeEntries(IEnumerable<SavedPlaylistEntry> entries,
            out List<SavedPlaylistEntry> normalized, out string error)
        {
            normalized = new List<SavedPlaylistEntry>();
            error = "";
            if (entries == null)
            {
                error = "The playlist videos could not be read.";
                return false;
            }
            foreach (var entry in entries)
            {
                if (entry == null || !IsVideoId(entry.VideoId))
                {
                    error = "Every playlist video must have a valid 11-character YouTube video ID.";
                    return false;
                }
                string title = entry.Title.Trim();
                if (title.Length > MaxTitleLength || !HasValidXmlCharacters(title))
                {
                    error = "A video title is too long or contains unsupported characters.";
                    return false;
                }
                normalized.Add(new SavedPlaylistEntry(entry.VideoId, title));
                if (normalized.Count > MaxEntries)
                {
                    error = "A saved playlist can contain up to " + MaxEntries + " videos.";
                    return false;
                }
            }
            // Empty collections can be created first, then filled from the playlist UI.
            return true;
        }

        private static bool IsVideoId(string value)
        {
            return value != null && value.Length == 11 && value.All(character =>
                (character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9') || character == '_' || character == '-');
        }

        private static bool HasValidXmlCharacters(string value)
        {
            try { XmlConvert.VerifyXmlChars(value); return true; }
            catch (XmlException) { return false; }
        }

        private static void RequireElementsOnly(XElement element)
        {
            if (element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                throw new InvalidDataException("The saved-playlist file contains unexpected text.");
        }

        private static bool SameBytes(byte[] first, byte[] second)
        {
            if (first == null || second == null) return first == second;
            return first.SequenceEqual(second);
        }

        private static bool IsStorageError(Exception exception)
        {
            return exception is IOException || exception is InvalidDataException || exception is UnauthorizedAccessException ||
                exception is XmlException || exception is ArgumentException ||
                exception is NotSupportedException || exception is System.Security.SecurityException;
        }
    }
}
