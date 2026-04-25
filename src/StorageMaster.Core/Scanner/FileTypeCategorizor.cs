using StorageMaster.Core.Models;

namespace StorageMaster.Core.Scanner;

/// <summary>Maps file extensions to coarse categories. Thread-safe after construction.</summary>
public static class FileTypeCategorizor
{
    private static readonly Dictionary<string, FileTypeCategory> _map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Documents
            [".pdf"]  = FileTypeCategory.Document,
            [".doc"]  = FileTypeCategory.Document,
            [".docx"] = FileTypeCategory.Document,
            [".xls"]  = FileTypeCategory.Document,
            [".xlsx"] = FileTypeCategory.Document,
            [".ppt"]  = FileTypeCategory.Document,
            [".pptx"] = FileTypeCategory.Document,
            [".odt"]  = FileTypeCategory.Document,
            [".ods"]  = FileTypeCategory.Document,
            [".txt"]  = FileTypeCategory.Document,
            [".rtf"]  = FileTypeCategory.Document,
            [".csv"]  = FileTypeCategory.Document,
            [".md"]   = FileTypeCategory.Document,

            // Images
            [".jpg"]  = FileTypeCategory.Image,
            [".jpeg"] = FileTypeCategory.Image,
            [".png"]  = FileTypeCategory.Image,
            [".gif"]  = FileTypeCategory.Image,
            [".bmp"]  = FileTypeCategory.Image,
            [".tiff"] = FileTypeCategory.Image,
            [".tif"]  = FileTypeCategory.Image,
            [".webp"] = FileTypeCategory.Image,
            [".svg"]  = FileTypeCategory.Image,
            [".ico"]  = FileTypeCategory.Image,
            [".raw"]  = FileTypeCategory.Image,
            [".cr2"]  = FileTypeCategory.Image,
            [".nef"]  = FileTypeCategory.Image,
            [".heic"] = FileTypeCategory.Image,

            // Video
            [".mp4"]  = FileTypeCategory.Video,
            [".mkv"]  = FileTypeCategory.Video,
            [".avi"]  = FileTypeCategory.Video,
            [".mov"]  = FileTypeCategory.Video,
            [".wmv"]  = FileTypeCategory.Video,
            [".flv"]  = FileTypeCategory.Video,
            [".webm"] = FileTypeCategory.Video,
            [".m4v"]  = FileTypeCategory.Video,
            [".mpg"]  = FileTypeCategory.Video,
            [".mpeg"] = FileTypeCategory.Video,

            // Audio
            [".mp3"]  = FileTypeCategory.Audio,
            [".flac"] = FileTypeCategory.Audio,
            [".wav"]  = FileTypeCategory.Audio,
            [".aac"]  = FileTypeCategory.Audio,
            [".ogg"]  = FileTypeCategory.Audio,
            [".wma"]  = FileTypeCategory.Audio,
            [".m4a"]  = FileTypeCategory.Audio,

            // Archives
            [".zip"]  = FileTypeCategory.Archive,
            [".rar"]  = FileTypeCategory.Archive,
            [".7z"]   = FileTypeCategory.Archive,
            [".tar"]  = FileTypeCategory.Archive,
            [".gz"]   = FileTypeCategory.Archive,
            [".bz2"]  = FileTypeCategory.Archive,
            [".xz"]   = FileTypeCategory.Archive,
            [".cab"]  = FileTypeCategory.Archive,

            // Executables
            [".exe"]  = FileTypeCategory.Executable,
            [".dll"]  = FileTypeCategory.Executable,
            [".sys"]  = FileTypeCategory.SystemFile,
            [".drv"]  = FileTypeCategory.SystemFile,
            [".ocx"]  = FileTypeCategory.Executable,
            [".com"]  = FileTypeCategory.Executable,
            [".scr"]  = FileTypeCategory.Executable,

            // Installers
            [".msi"]  = FileTypeCategory.Installer,
            [".msp"]  = FileTypeCategory.Installer,
            [".msix"] = FileTypeCategory.Installer,
            [".appx"] = FileTypeCategory.Installer,

            // Source code
            [".cs"]   = FileTypeCategory.SourceCode,
            [".cpp"]  = FileTypeCategory.SourceCode,
            [".c"]    = FileTypeCategory.SourceCode,
            [".h"]    = FileTypeCategory.SourceCode,
            [".java"] = FileTypeCategory.SourceCode,
            [".py"]   = FileTypeCategory.SourceCode,
            [".js"]   = FileTypeCategory.SourceCode,
            [".ts"]   = FileTypeCategory.SourceCode,
            [".go"]   = FileTypeCategory.SourceCode,
            [".rs"]   = FileTypeCategory.SourceCode,

            // Databases
            [".db"]   = FileTypeCategory.Database,
            [".sqlite"] = FileTypeCategory.Database,
            [".mdf"]  = FileTypeCategory.Database,
            [".ldf"]  = FileTypeCategory.Database,
            [".accdb"] = FileTypeCategory.Database,

            // Temp / Cache
            [".tmp"]  = FileTypeCategory.Temporary,
            [".temp"] = FileTypeCategory.Temporary,
            [".bak"]  = FileTypeCategory.Temporary,
            [".old"]  = FileTypeCategory.Temporary,
            [".chk"]  = FileTypeCategory.Temporary,

            // Logs
            [".log"]  = FileTypeCategory.Log,
            [".evtx"] = FileTypeCategory.Log,
            [".etl"]  = FileTypeCategory.Log,
        };

    public static FileTypeCategory Categorize(string extension) =>
        _map.TryGetValue(extension, out var cat) ? cat : FileTypeCategory.Unknown;
}
