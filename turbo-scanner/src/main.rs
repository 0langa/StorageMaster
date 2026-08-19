/// StorageMaster Turbo Scanner
///
/// A high-performance parallel file system enumerator built on jwalk.
/// jwalk uses a work-stealing thread pool to walk directory trees in parallel,
/// which is significantly faster than sequential .NET enumeration on multi-core
/// systems and fast storage (NVMe / SSDs).
///
/// Output: one JSON object per line (JSONL) on stdout.
/// Format: {"path":"...","size":N,"modified_unix":N,"created_unix":N,
///          "modified_utc_ticks":N,"created_utc_ticks":N,"attributes":N,
///          "volume_serial":N|null,"file_index":N|null,
///          "is_dir":false,"is_hidden":false}
///
/// Contract v2: adds `is_hidden` (Windows Hidden attribute; dot-name on Unix) and
/// attribute-based `--skip-hidden` semantics. With `--skip-hidden`, hidden
/// directories are pruned during enumeration so their contents are never
/// reported — matching the managed scanner's EnumerationOptions.AttributesToSkip
/// behavior. The scan root itself is always walked, like the managed scanner.
///
/// Contract v3 adds exact UTC timestamps as 100-nanosecond ticks since
/// 0001-01-01, raw Windows file attributes, and nullable Windows volume/file
/// identity. The second-resolution Unix fields remain for compatibility with
/// older managed clients.
///
/// Descendant access and I/O failures are written to stderr and do not abort
/// the scan. Fatal root, enumeration setup, and output failures return nonzero.
/// A file whose attributes cannot be read through an open handle — exclusively
/// locked or ACL-denied — is still reported from its enumeration metadata with
/// `volume_serial` and `file_index` null, rather than dropped from the scan.
///
/// All per-entry metadata is captured inside jwalk's `process_read_dir`
/// callback, which runs on the walker threads. The drain loop issues no
/// syscalls, so `--threads` scales the part of the scan that actually costs.
use clap::Parser;
use jwalk::{Parallelism, WalkDirGeneric};
use serde::Serialize;
use std::io::{self, BufWriter, Write};
use std::path::Path;
use std::process::ExitCode;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const DOTNET_TICKS_AT_UNIX_EPOCH: i128 = 621_355_968_000_000_000;
const TICKS_PER_SECOND: i128 = 10_000_000;
const NANOS_PER_TICK: u32 = 100;
#[cfg(windows)]
const DOTNET_TICKS_AT_WINDOWS_EPOCH: i128 = 504_911_232_000_000_000;

#[derive(Parser, Debug)]
#[command(name = "turbo-scanner", version, about = "StorageMaster Turbo Scanner")]
struct Args {
    /// Root directory to scan
    #[arg(short, long)]
    path: String,

    /// Number of parallel threads (0 = number of logical CPU cores)
    #[arg(short, long, default_value_t = 0)]
    threads: usize,

    /// Minimum file size in bytes to report (0 = all files)
    #[arg(long, default_value_t = 0)]
    min_size: u64,

    /// Skip hidden files and directories (Windows Hidden attribute; dot-names on Unix)
    #[arg(long, default_value_t = false)]
    skip_hidden: bool,

    /// Follow symbolic links and junctions. Disabled unless explicitly requested.
    #[arg(long, default_value_t = false)]
    follow_links: bool,
}

#[derive(Serialize)]
struct FileRecord<'a> {
    path: &'a str,
    size: u64,
    modified_unix: i64,
    created_unix: i64,
    modified_utc_ticks: i64,
    created_utc_ticks: i64,
    attributes: u32,
    volume_serial: Option<u32>,
    file_index: Option<u64>,
    is_dir: bool,
    is_hidden: bool,
}

#[derive(Debug)]
struct CapturedFileMetadata {
    size: u64,
    modified_unix: i64,
    created_unix: i64,
    modified_utc_ticks: i64,
    created_utc_ticks: i64,
    attributes: u32,
    volume_serial: Option<u32>,
    file_index: Option<u64>,
    is_hidden: bool,
}

/// Per-entry capture, carried from the walker threads to the drain loop through
/// jwalk's `DirEntry::client_state`.
///
/// jwalk parallelises `fs::read_dir` and the `process_read_dir` callback only;
/// iterating the walker runs on the calling thread. Capturing in the callback
/// and reading the result here is what lets `--threads` scale the metadata
/// syscalls, which dominate the scan.
#[derive(Debug, Default)]
struct EntryCapture {
    metadata: Option<CapturedFileMetadata>,
    /// Emitted by the drain loop, which knows the entry's depth and therefore
    /// whether the failure is fatal.
    warning: Option<String>,
}

impl EntryCapture {
    fn captured(metadata: CapturedFileMetadata) -> Self {
        Self {
            metadata: Some(metadata),
            warning: None,
        }
    }

    fn failed(warning: String) -> Self {
        Self {
            metadata: None,
            warning: Some(warning),
        }
    }

    /// True when the walk callback never touched this entry.
    fn is_unvisited(&self) -> bool {
        self.metadata.is_none() && self.warning.is_none()
    }
}

#[cfg(windows)]
fn is_hidden(metadata: &std::fs::Metadata, _path: &Path) -> bool {
    use std::os::windows::fs::MetadataExt;
    const FILE_ATTRIBUTE_HIDDEN: u32 = 0x2;
    metadata.file_attributes() & FILE_ATTRIBUTE_HIDDEN != 0
}

#[cfg(windows)]
fn raw_file_attributes(metadata: &std::fs::Metadata) -> u32 {
    use std::os::windows::fs::MetadataExt;
    metadata.file_attributes()
}

#[cfg(not(windows))]
fn raw_file_attributes(_metadata: &std::fs::Metadata) -> u32 {
    0
}

#[cfg(windows)]
fn is_reparse_point(metadata: &std::fs::Metadata) -> bool {
    const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
    raw_file_attributes(metadata) & FILE_ATTRIBUTE_REPARSE_POINT != 0
}

#[cfg(windows)]
#[repr(C)]
#[derive(Default)]
struct WindowsFileTime {
    low_date_time: u32,
    high_date_time: u32,
}

#[cfg(windows)]
#[repr(C)]
#[derive(Default)]
struct ByHandleFileInformation {
    file_attributes: u32,
    creation_time: WindowsFileTime,
    last_access_time: WindowsFileTime,
    last_write_time: WindowsFileTime,
    volume_serial_number: u32,
    file_size_high: u32,
    file_size_low: u32,
    number_of_links: u32,
    file_index_high: u32,
    file_index_low: u32,
}

#[cfg(windows)]
#[link(name = "kernel32")]
extern "system" {
    fn GetFileInformationByHandle(
        file: *mut std::ffi::c_void,
        information: *mut ByHandleFileInformation,
    ) -> i32;
}

#[cfg(windows)]
fn capture_file_metadata(path: &Path) -> io::Result<CapturedFileMetadata> {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;

    const FILE_READ_ATTRIBUTES: u32 = 0x80;
    const FILE_SHARE_READ: u32 = 0x1;
    const FILE_SHARE_WRITE: u32 = 0x2;
    const FILE_SHARE_DELETE: u32 = 0x4;
    const FILE_ATTRIBUTE_DIRECTORY: u32 = 0x10;
    const FILE_ATTRIBUTE_HIDDEN: u32 = 0x2;

    let file = std::fs::OpenOptions::new()
        .access_mode(FILE_READ_ATTRIBUTES)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .open(path)?;
    let mut information = ByHandleFileInformation::default();
    // SAFETY: file owns a valid Windows handle and information points to a
    // correctly laid-out writable BY_HANDLE_FILE_INFORMATION buffer.
    let succeeded =
        unsafe { GetFileInformationByHandle(file.as_raw_handle(), &mut information) } != 0;
    if !succeeded {
        return Err(io::Error::last_os_error());
    }

    if information.file_attributes & FILE_ATTRIBUTE_DIRECTORY != 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "file path changed into a directory during metadata capture",
        ));
    }

    let modified_utc_ticks = windows_filetime_to_utc_ticks(&information.last_write_time)?;
    let created_utc_ticks = windows_filetime_to_utc_ticks(&information.creation_time)?;
    let size = (u64::from(information.file_size_high) << 32) | u64::from(information.file_size_low);

    let file_index =
        (u64::from(information.file_index_high) << 32) | u64::from(information.file_index_low);
    Ok(CapturedFileMetadata {
        size,
        modified_unix: utc_ticks_to_unix_seconds(modified_utc_ticks),
        created_unix: utc_ticks_to_unix_seconds(created_utc_ticks),
        modified_utc_ticks,
        created_utc_ticks,
        attributes: information.file_attributes,
        volume_serial: Some(information.volume_serial_number),
        file_index: Some(file_index),
        is_hidden: information.file_attributes & FILE_ATTRIBUTE_HIDDEN != 0,
    })
}

#[cfg(not(windows))]
fn capture_file_metadata(path: &Path) -> io::Result<CapturedFileMetadata> {
    let metadata = std::fs::metadata(path)?;
    if metadata.is_dir() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "file path changed into a directory during metadata capture",
        ));
    }

    let modified = metadata.modified();
    let modified_unix = system_time_to_unix_seconds(&modified);
    let modified_utc_ticks = system_time_to_utc_ticks(modified);
    let created = metadata.created();
    let created_unix = system_time_to_unix_seconds(&created);
    let created_utc_ticks = system_time_to_utc_ticks(created);

    Ok(CapturedFileMetadata {
        size: metadata.len(),
        modified_unix,
        created_unix,
        modified_utc_ticks,
        created_utc_ticks,
        attributes: 0,
        volume_serial: None,
        file_index: None,
        is_hidden: is_hidden(&metadata, path),
    })
}

#[cfg(not(windows))]
fn is_reparse_point(metadata: &std::fs::Metadata) -> bool {
    metadata.file_type().is_symlink()
}

#[cfg(not(windows))]
fn is_hidden(_metadata: &std::fs::Metadata, path: &Path) -> bool {
    path.file_name()
        .and_then(|n| n.to_str())
        .is_some_and(|n| n.starts_with('.'))
}

fn duration_to_ticks(duration: Duration, round_up_sub_tick: bool) -> Option<i128> {
    let whole_ticks = i128::from(duration.as_secs()).checked_mul(TICKS_PER_SECOND)?;
    let nanos = duration.subsec_nanos();
    let fractional_ticks = if round_up_sub_tick {
        i128::from(nanos.div_ceil(NANOS_PER_TICK))
    } else {
        i128::from(nanos / NANOS_PER_TICK)
    };
    whole_ticks.checked_add(fractional_ticks)
}

fn system_time_to_utc_ticks(time: io::Result<SystemTime>) -> i64 {
    let Ok(time) = time else {
        return 0;
    };

    let ticks = match time.duration_since(UNIX_EPOCH) {
        Ok(duration) => duration_to_ticks(duration, false)
            .and_then(|value| DOTNET_TICKS_AT_UNIX_EPOCH.checked_add(value)),
        Err(error) => duration_to_ticks(error.duration(), true)
            .and_then(|value| DOTNET_TICKS_AT_UNIX_EPOCH.checked_sub(value)),
    };

    ticks
        .and_then(|value| i64::try_from(value).ok())
        .unwrap_or(0)
}

fn system_time_to_unix_seconds(time: &io::Result<SystemTime>) -> i64 {
    time.as_ref()
        .ok()
        .and_then(|value| value.duration_since(UNIX_EPOCH).ok())
        .and_then(|duration| i64::try_from(duration.as_secs()).ok())
        .unwrap_or(0)
}

#[cfg(windows)]
fn windows_filetime_to_utc_ticks(time: &WindowsFileTime) -> io::Result<i64> {
    let filetime = (u64::from(time.high_date_time) << 32) | u64::from(time.low_date_time);
    DOTNET_TICKS_AT_WINDOWS_EPOCH
        .checked_add(i128::from(filetime))
        .and_then(|ticks| i64::try_from(ticks).ok())
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidData,
                "Windows timestamp is out of range",
            )
        })
}

#[cfg(windows)]
fn utc_ticks_to_unix_seconds(ticks: i64) -> i64 {
    let seconds = (i128::from(ticks) - DOTNET_TICKS_AT_UNIX_EPOCH).div_euclid(TICKS_PER_SECOND);
    i64::try_from(seconds).unwrap_or(0)
}

/// Builds a record from enumeration metadata. `named` always describes the
/// entry's own name rather than a link target: the raw attributes and the
/// Hidden bit have to come from the name that was enumerated.
fn captured_from_metadata(
    path: &Path,
    timestamps: &std::fs::Metadata,
    named: &std::fs::Metadata,
    size: u64,
) -> CapturedFileMetadata {
    let modified = timestamps.modified();
    let modified_unix = system_time_to_unix_seconds(&modified);
    let modified_utc_ticks = system_time_to_utc_ticks(modified);
    let created = timestamps.created();
    let created_unix = system_time_to_unix_seconds(&created);
    let created_utc_ticks = system_time_to_utc_ticks(created);

    CapturedFileMetadata {
        size,
        modified_unix,
        created_unix,
        modified_utc_ticks,
        created_utc_ticks,
        attributes: raw_file_attributes(named),
        volume_serial: None,
        file_index: None,
        is_hidden: is_hidden(named, path),
    }
}

/// One stat per directory. jwalk's `DirEntry::metadata()` is literally
/// `fs::symlink_metadata(path)` for every entry that is not a followed link, so
/// the walk metadata and the named metadata used to be two identical syscalls;
/// only a followed link can make the two differ.
fn capture_directory_metadata(path: &Path, follow_link: bool) -> io::Result<CapturedFileMetadata> {
    let named = std::fs::symlink_metadata(path)?;
    let followed = if follow_link {
        std::fs::metadata(path).ok()
    } else {
        None
    };

    Ok(captured_from_metadata(
        path,
        followed.as_ref().unwrap_or(&named),
        &named,
        0,
    ))
}

/// A file whose attributes cannot be read through an open handle — exclusively
/// locked (pagefile.sys, registry hives) or ACL-denied — still occupies bytes on
/// disk. Report it from the enumeration metadata, which std backs with a
/// `FindFirstFileExW` fallback, instead of dropping the record and
/// under-reporting disk usage. Identity stays null, which the managed consumer
/// already reads as identity-less and which keeps the file out of every
/// deletion path.
fn degraded_file_capture(path: &Path, error: &io::Error) -> EntryCapture {
    match std::fs::symlink_metadata(path) {
        Ok(metadata) if !metadata.is_dir() => EntryCapture {
            metadata: Some(captured_from_metadata(
                path,
                &metadata,
                &metadata,
                metadata.len(),
            )),
            warning: Some(format!(
                "cannot capture stable file identity '{}': {error}; reporting size and attributes only",
                path.display()
            )),
        },
        _ => EntryCapture::failed(format!(
            "cannot capture stable file metadata '{}': {error}",
            path.display()
        )),
    }
}

/// All per-entry I/O of the scan. Called from `process_read_dir` on the walker
/// threads; the drain loop only reads what this produced.
fn capture_entry(path: &Path, is_dir: bool, follow_link: bool) -> EntryCapture {
    if is_dir {
        match capture_directory_metadata(path, follow_link) {
            Ok(metadata) => EntryCapture::captured(metadata),
            Err(error) => EntryCapture::failed(format!("{} — {error}", path.display())),
        }
    } else {
        match capture_file_metadata(path) {
            Ok(metadata) => EntryCapture::captured(metadata),
            Err(error) => degraded_file_capture(path, &error),
        }
    }
}

fn validate_no_follow_root(path: &Path) -> io::Result<()> {
    for ancestor in path.ancestors().collect::<Vec<_>>().into_iter().rev() {
        if ancestor.as_os_str().is_empty() {
            continue;
        }

        let metadata = std::fs::symlink_metadata(ancestor).map_err(|error| {
            io::Error::new(
                error.kind(),
                format!(
                    "cannot inspect scan root ancestry '{}': {error}",
                    ancestor.display()
                ),
            )
        })?;
        if is_reparse_point(&metadata) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!(
                    "scan root ancestry contains a reparse point: '{}'; pass --follow-links to opt in",
                    ancestor.display()
                ),
            ));
        }
    }

    Ok(())
}

fn scan(args: &Args, out: &mut impl Write) -> io::Result<()> {
    let root_path = Path::new(&args.path);
    let named_root_metadata = std::fs::symlink_metadata(root_path).map_err(|error| {
        io::Error::new(
            error.kind(),
            format!("cannot access scan root '{}': {error}", args.path),
        )
    })?;
    if !args.follow_links {
        validate_no_follow_root(root_path)?;
    }

    let root_metadata = if args.follow_links {
        std::fs::metadata(root_path).map_err(|error| {
            io::Error::new(
                error.kind(),
                format!("cannot access scan root '{}': {error}", args.path),
            )
        })?
    } else {
        named_root_metadata
    };
    if !root_metadata.is_dir() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            format!("scan root is not a directory: '{}'", args.path),
        ));
    }

    // 0 threads → use all logical cores via jwalk's default
    let num_threads = if args.threads == 0 {
        std::thread::available_parallelism()
            .map(|n| n.get())
            .unwrap_or(4)
    } else {
        args.threads
    };

    let parallelism = if num_threads <= 1 {
        Parallelism::Serial
    } else {
        Parallelism::RayonNewPool(num_threads)
    };

    let prune_hidden = args.skip_hidden;
    let follow_links = args.follow_links;
    // jwalk's built-in skip_hidden is dot-name based; attribute-based pruning
    // below matches the managed scanner, so the built-in stays off.
    let walker = WalkDirGeneric::<((), EntryCapture)>::new(&args.path)
        .parallelism(parallelism)
        .follow_links(args.follow_links)
        .skip_hidden(false)
        .process_read_dir(move |depth, _dir_path, _state, children| {
            // Every entry of the walk passes through this callback exactly once,
            // and this callback is the only part jwalk runs in parallel. All the
            // per-entry metadata I/O therefore lives here rather than in the
            // drain loop, which is single-threaded.
            for child in children.iter_mut() {
                let Ok(entry) = child else { continue };
                // Symlinked entries are dropped by the drain loop unless links
                // are followed, so don't pay for their metadata.
                let is_symlink = entry.path_is_symlink();
                if is_symlink && !follow_links {
                    continue;
                }
                let path = entry.path();
                let is_dir = entry.file_type().is_dir();
                entry.client_state = capture_entry(&path, is_dir, is_symlink && follow_links);
            }

            if !prune_hidden {
                return;
            }
            // jwalk invokes this once with `depth == None` for a synthetic read
            // whose only child is the scan root itself. Filtering there prunes
            // the whole walk: real drive roots such as `C:\` carry the Hidden
            // and System attributes, so `--skip-hidden` scans of a whole drive
            // silently returned zero files. The scan root is chosen explicitly
            // by the caller and is always walked.
            if depth.is_none() {
                return;
            }
            // Dropping a hidden directory here prevents descent, so files
            // inside hidden directories are never reported. The Hidden bit comes
            // from the capture above instead of a third stat per child.
            children.retain(|child| match child {
                Ok(entry) => entry
                    .client_state
                    .metadata
                    .as_ref()
                    // unreadable entries surface as WARN later
                    .is_none_or(|metadata| !metadata.is_hidden),
                Err(_) => true, // keep errors so they are reported downstream
            });
        })
        .try_into_iter()
        .map_err(|error| {
            io::Error::other(format!("cannot initialize directory enumeration: {error}"))
        })?;

    for entry in walker {
        let mut entry = match entry {
            Ok(e) => e,
            Err(e) => {
                if e.depth() == 0 {
                    return Err(io::Error::other(format!("cannot enumerate scan root: {e}")));
                }
                eprintln!("WARN: {e}");
                continue;
            }
        };

        let is_symlink = entry.path_is_symlink();
        if is_symlink && !args.follow_links {
            continue;
        }

        let path_buf = entry.path();
        // file_type() is copied out of the enumeration result and costs no
        // syscall, unlike the metadata() call it replaced.
        let is_dir = entry.file_type().is_dir();
        let mut capture = std::mem::take(&mut entry.client_state);
        if capture.is_unvisited() {
            // Defensive: every entry passes through process_read_dir, so this
            // only guards against dropping a record if that ever stops holding.
            capture = capture_entry(&path_buf, is_dir, is_symlink && args.follow_links);
        }

        if let Some(warning) = capture.warning {
            if entry.depth() == 0 && capture.metadata.is_none() {
                return Err(io::Error::other(format!(
                    "cannot read scan root metadata: {warning}"
                )));
            }
            eprintln!("WARN: {warning}");
        }

        let Some(captured) = capture.metadata else {
            continue;
        };

        if !is_dir && captured.size < args.min_size {
            continue;
        }

        let path_str = path_buf.to_string_lossy();
        let record = FileRecord {
            path: path_str.as_ref(),
            size: captured.size,
            modified_unix: captured.modified_unix,
            created_unix: captured.created_unix,
            modified_utc_ticks: captured.modified_utc_ticks,
            created_utc_ticks: captured.created_utc_ticks,
            attributes: captured.attributes,
            volume_serial: captured.volume_serial,
            file_index: captured.file_index,
            is_dir,
            is_hidden: captured.is_hidden,
        };

        // Rendered straight into the BufWriter; the intermediate String this
        // replaced was one heap allocation per record. A writer failure has to
        // keep its own ErrorKind so a broken stdout pipe stays fatal.
        serde_json::to_writer(&mut *out, &record).map_err(|error| match error.io_error_kind() {
            Some(kind) => io::Error::new(kind, error),
            None => io::Error::new(io::ErrorKind::InvalidData, error),
        })?;
        out.write_all(b"\n")?;
    }

    // Flush remaining buffered output before exit.
    out.flush()
}

fn main() -> ExitCode {
    let args = Args::parse();

    let stdout = io::stdout();
    let mut writer = BufWriter::with_capacity(256 * 1024, stdout.lock());
    match scan(&args, &mut writer) {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("ERROR: {error}");
            ExitCode::FAILURE
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::Parser;

    #[test]
    fn args_use_safe_defaults() {
        let args = Args::try_parse_from(["turbo-scanner", "--path", r"C:\data"]).unwrap();

        assert_eq!(args.path, r"C:\data");
        assert_eq!(args.threads, 0);
        assert_eq!(args.min_size, 0);
        assert!(!args.skip_hidden);
        assert!(!args.follow_links);
    }

    #[test]
    fn args_accept_explicit_scanner_controls() {
        let args = Args::try_parse_from([
            "turbo-scanner",
            "--path",
            r"C:\data",
            "--threads",
            "6",
            "--min-size",
            "4096",
            "--skip-hidden",
            "--follow-links",
        ])
        .unwrap();

        assert_eq!(args.threads, 6);
        assert_eq!(args.min_size, 4096);
        assert!(args.skip_hidden);
        assert!(args.follow_links);
    }

    #[test]
    fn follow_links_requires_an_explicit_flag() {
        let default_args = Args::try_parse_from(["turbo-scanner", "--path", r"C:\data"]);
        assert!(!default_args.unwrap().follow_links);

        let follow_args =
            Args::try_parse_from(["turbo-scanner", "--path", r"C:\data", "--follow-links"]);
        assert!(follow_args.unwrap().follow_links);
    }

    #[test]
    fn version_flag_reports_package_version() {
        let version = Args::try_parse_from(["turbo-scanner", "--version"]).unwrap_err();

        assert_eq!(version.kind(), clap::error::ErrorKind::DisplayVersion);
        assert_eq!(version.to_string().trim(), "turbo-scanner 2.2.1");
    }

    #[test]
    fn file_record_serializes_to_the_jsonl_contract() {
        let record = FileRecord {
            path: r"C:\data\file.txt",
            size: 42,
            modified_unix: 100,
            created_unix: 90,
            modified_utc_ticks: 621_355_969_000_000_000,
            created_utc_ticks: 621_355_968_900_000_000,
            attributes: 34,
            volume_serial: Some(0xA1B2_C3D4),
            file_index: Some(42),
            is_dir: false,
            is_hidden: true,
        };

        let value = serde_json::to_value(record).unwrap();

        assert_eq!(value["path"], r"C:\data\file.txt");
        assert_eq!(value["size"], 42);
        assert_eq!(value["modified_unix"], 100);
        assert_eq!(value["created_unix"], 90);
        assert_eq!(value["modified_utc_ticks"], 621_355_969_000_000_000_i64);
        assert_eq!(value["created_utc_ticks"], 621_355_968_900_000_000_i64);
        assert_eq!(value["attributes"], 34);
        assert_eq!(value["volume_serial"], 0xA1B2_C3D4_u32);
        assert_eq!(value["file_index"], 42);
        assert_eq!(value["is_dir"], false);
        assert_eq!(value["is_hidden"], true);
    }

    fn scan_paths(args: &Args) -> Vec<String> {
        let mut buffer = Vec::new();
        scan(args, &mut buffer).unwrap();
        String::from_utf8(buffer)
            .unwrap()
            .lines()
            .map(|line| {
                serde_json::from_str::<serde_json::Value>(line).unwrap()["path"]
                    .as_str()
                    .unwrap()
                    .to_string()
            })
            .collect()
    }

    #[test]
    fn missing_root_is_a_fatal_scan_error() {
        let missing = std::env::temp_dir().join(format!(
            "turbo_missing_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let _ = std::fs::remove_dir_all(&missing);
        let args = Args {
            path: missing.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: false,
            follow_links: false,
        };

        let error = scan(&args, &mut Vec::new()).unwrap_err();

        assert_eq!(error.kind(), io::ErrorKind::NotFound);
        assert!(error.to_string().contains("cannot access scan root"));
    }

    struct FailingWriter;

    impl Write for FailingWriter {
        fn write(&mut self, _buffer: &[u8]) -> io::Result<usize> {
            Err(io::Error::new(io::ErrorKind::BrokenPipe, "test failure"))
        }

        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    #[test]
    fn output_failure_is_a_fatal_scan_error() {
        let root = std::env::temp_dir().join(format!("turbo_output_{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let args = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: false,
            follow_links: false,
        };

        let error = scan(&args, &mut FailingWriter).unwrap_err();

        assert_eq!(error.kind(), io::ErrorKind::BrokenPipe);
        let _ = std::fs::remove_dir_all(&root);
    }

    #[cfg(windows)]
    fn set_hidden(path: &Path) {
        let status = std::process::Command::new("attrib")
            .arg("+H")
            .arg(path)
            .status()
            .expect("attrib should run");
        assert!(status.success(), "attrib +H should succeed");
    }

    #[cfg(windows)]
    #[test]
    fn skip_hidden_prunes_hidden_files_and_directory_contents() {
        let root = std::env::temp_dir().join(format!("turbo_hidden_{}", std::process::id()));
        let hidden_dir = root.join("hidden-dir");
        std::fs::create_dir_all(&hidden_dir).unwrap();
        std::fs::write(root.join("visible.txt"), b"v").unwrap();
        std::fs::write(root.join("hidden.txt"), b"h").unwrap();
        std::fs::write(hidden_dir.join("inside.txt"), b"i").unwrap();
        set_hidden(&root.join("hidden.txt"));
        set_hidden(&hidden_dir);

        let args = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: true,
            follow_links: false,
        };
        let paths = scan_paths(&args);

        assert!(paths.iter().any(|p| p.ends_with("visible.txt")));
        assert!(!paths.iter().any(|p| p.ends_with("hidden.txt")));
        assert!(!paths.iter().any(|p| p.ends_with("hidden-dir")));
        assert!(!paths.iter().any(|p| p.ends_with("inside.txt")));

        let args_inclusive = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: false,
            follow_links: false,
        };
        let all_paths = scan_paths(&args_inclusive);
        assert!(all_paths.iter().any(|p| p.ends_with("hidden.txt")));
        assert!(all_paths.iter().any(|p| p.ends_with("inside.txt")));

        let _ = std::fs::remove_dir_all(&root);
    }

    /// A hidden scan root must still be walked. jwalk hands the root itself to
    /// `process_read_dir` as a synthetic depth-`None` child, so a naive hidden
    /// filter prunes the entire tree. Real drive roots such as `C:\` carry the
    /// Hidden and System attributes, which made `--skip-hidden` scans of a whole
    /// drive report success with zero files.
    #[cfg(windows)]
    #[test]
    fn skip_hidden_still_walks_a_hidden_scan_root() {
        let root = std::env::temp_dir().join(format!("turbo_hidden_root_{}", std::process::id()));
        let child_dir = root.join("child-dir");
        std::fs::create_dir_all(&child_dir).unwrap();
        std::fs::write(root.join("visible.txt"), b"v").unwrap();
        std::fs::write(child_dir.join("nested.txt"), b"n").unwrap();
        set_hidden(&root);

        let args = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: true,
            follow_links: false,
        };
        let paths = scan_paths(&args);

        assert!(
            paths.iter().any(|p| p.ends_with("visible.txt")),
            "a hidden scan root must not prune its own contents: {paths:?}"
        );
        assert!(
            paths.iter().any(|p| p.ends_with("nested.txt")),
            "descendants of a hidden scan root must still be walked: {paths:?}"
        );

        let _ = std::fs::remove_dir_all(&root);
    }

    #[cfg(windows)]
    #[test]
    fn hidden_attribute_is_reported_in_records() {
        let root = std::env::temp_dir().join(format!("turbo_attr_{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        std::fs::write(root.join("plain.txt"), b"p").unwrap();
        std::fs::write(root.join("shy.txt"), b"s").unwrap();
        set_hidden(&root.join("shy.txt"));

        let args = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: false,
            follow_links: false,
        };
        let mut buffer = Vec::new();
        scan(&args, &mut buffer).unwrap();
        let records: Vec<serde_json::Value> = String::from_utf8(buffer)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();

        let plain = records
            .iter()
            .find(|r| r["path"].as_str().unwrap().ends_with("plain.txt"))
            .unwrap();
        let shy = records
            .iter()
            .find(|r| r["path"].as_str().unwrap().ends_with("shy.txt"))
            .unwrap();
        assert_eq!(plain["is_hidden"], false);
        assert_eq!(shy["is_hidden"], true);

        let _ = std::fs::remove_dir_all(&root);
    }

    fn build_tree(root: &Path) {
        for index in 0..4 {
            let dir = root.join(format!("dir-{index}"));
            std::fs::create_dir_all(&dir).unwrap();
            for file in 0..5 {
                std::fs::write(dir.join(format!("file-{file}.bin")), vec![b'x'; file + 1]).unwrap();
            }
        }
    }

    /// Metadata capture moved onto the walker threads, where it runs
    /// concurrently and in read_dir order rather than in the drain loop. jwalk
    /// yields strictly ordered results, so the byte stream must not depend on
    /// how many threads produced it.
    #[test]
    fn parallel_and_serial_walks_emit_identical_records() {
        let root = std::env::temp_dir().join(format!("turbo_threads_{}", std::process::id()));
        build_tree(&root);

        let scan_with = |threads| {
            let args = Args {
                path: root.to_string_lossy().into_owned(),
                threads,
                min_size: 0,
                skip_hidden: false,
                follow_links: false,
            };
            let mut buffer = Vec::new();
            scan(&args, &mut buffer).unwrap();
            String::from_utf8(buffer).unwrap()
        };

        let serial = scan_with(1);
        let parallel = scan_with(4);

        assert_eq!(serial.lines().count(), 1 + 4 + 20, "root, dirs and files");
        assert_eq!(serial, parallel);

        let _ = std::fs::remove_dir_all(&root);
    }

    /// A file that cannot be opened for attributes is still occupying disk
    /// space; dropping its record under-reported the scan total.
    #[test]
    fn an_unopenable_file_is_reported_without_identity() {
        let root = std::env::temp_dir().join(format!("turbo_degraded_{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let file = root.join("payload.bin");
        std::fs::write(&file, b"0123456789").unwrap();

        // 32 == ERROR_SHARING_VIOLATION, what an exclusively locked file returns.
        let capture = degraded_file_capture(&file, &io::Error::from_raw_os_error(32));

        let metadata = capture
            .metadata
            .expect("a file that cannot be opened must still be reported");
        assert_eq!(metadata.size, 10);
        assert!(
            metadata.volume_serial.is_none() && metadata.file_index.is_none(),
            "identity is reported as unavailable, never invented"
        );
        assert!(
            capture.warning.is_some(),
            "the degradation must still reach stderr"
        );

        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn a_vanished_file_is_not_reported() {
        let missing = std::env::temp_dir().join(format!(
            "turbo_vanished_{}_{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));

        let capture = degraded_file_capture(&missing, &io::Error::from_raw_os_error(2));

        assert!(capture.metadata.is_none(), "there is nothing to report");
        assert!(capture.warning.is_some());
    }

    #[cfg(windows)]
    fn run_icacls(path: &Path, arguments: &[&str]) -> bool {
        std::process::Command::new("icacls")
            .arg(path)
            .args(arguments)
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .status()
            .map(|status| status.success())
            .unwrap_or(false)
    }

    /// End-to-end cover for the degraded capture: a DACL that grants only
    /// SYSTEM makes the FILE_READ_ATTRIBUTES open fail while enumeration still
    /// sees the file. That is the same shape as pagefile.sys and the locked
    /// registry hives, whose records used to vanish from turbo scans and take
    /// their bytes out of the reported disk usage with them.
    #[cfg(windows)]
    #[test]
    fn files_that_cannot_be_opened_stay_in_the_scan() {
        let root = std::env::temp_dir().join(format!("turbo_denied_{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let denied = root.join("denied.bin");
        std::fs::write(&denied, b"0123456789").unwrap();
        let restricted = run_icacls(&denied, &["/inheritance:r", "/grant:r", "*S-1-5-18:(F)"]);
        // A process running as SYSTEM, or an environment without icacls, cannot
        // be locked out of its own file; skip rather than fail there.
        let denied_to_us = restricted && capture_file_metadata(&denied).is_err();

        let args = Args {
            path: root.to_string_lossy().into_owned(),
            threads: 1,
            min_size: 0,
            skip_hidden: false,
            follow_links: false,
        };
        let mut buffer = Vec::new();
        let result = scan(&args, &mut buffer);

        run_icacls(&denied, &["/reset"]);
        let _ = std::fs::remove_dir_all(&root);
        result.unwrap();
        if !denied_to_us {
            return;
        }

        let records: Vec<serde_json::Value> = String::from_utf8(buffer)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        let record = records
            .iter()
            .find(|record| record["path"].as_str().unwrap().ends_with("denied.bin"))
            .expect("a file that cannot be opened must still be reported");

        assert_eq!(record["size"], 10, "its bytes still count against the disk");
        assert!(record["volume_serial"].is_null());
        assert!(record["file_index"].is_null());
    }
}
